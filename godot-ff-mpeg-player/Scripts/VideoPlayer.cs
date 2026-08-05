using Godot;
using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

public partial class VideoPlayer : Control
{
	private struct YuvTexturePlane
	{
		public Image Image;
		public ImageTexture Texture;

		public static YuvTexturePlane Create(int width, int height)
		{
			var plane = new YuvTexturePlane();
			plane.Image = Image.CreateEmpty(width, height, false, Image.Format.R8);
			plane.Texture = ImageTexture.CreateFromImage(plane.Image);
			return plane;
		}

		public void Update(int width, int height, byte[] buffer)
		{
			Image.SetData(width, height, false, Image.Format.R8, buffer);
			Texture.Update(Image);
		}
	}

	private YuvTexturePlane _yPlane;
	private YuvTexturePlane _uPlane;
	private YuvTexturePlane _vPlane;

	private const string DllName = "FFMpegInterface";

	[Export]
	private TextureRect _videoDisplay;

	[Export] 
	private AudioStreamPlayer _audioPlayer;

	static VideoPlayer()
	{
		// Register custom DLL loader for native libraries
		NativeLibrary.SetDllImportResolver(typeof(VideoPlayer).Assembly, ResolveNativeLibrary);
	}

	private static IntPtr ResolveNativeLibrary(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
	{
		if (libraryName == DllName)
		{
			// Point to your relative subfolder
			string relativePath = Path.Combine("native", "x64", $"{libraryName}.dll");
			string fullPath = Path.Combine(Directory.GetCurrentDirectory(), relativePath);

			if (File.Exists(fullPath))
			{
				return NativeLibrary.Load(fullPath);
			}
		}

		// Fallback to default loading if not matched
		return IntPtr.Zero;
	}

	#region File Dialog
	public override void _Input(InputEvent @event)
	{
		if (@event is InputEventKey eventKey && eventKey.Pressed)
		{
			if (eventKey.Keycode == Key.O)
			{
				GD.Print("Open Video File...");
				OpenVideoFile();
			}
		}
	}

	protected FileDialog _videoDialog = null;
	protected FileDialog VideoDialog
	{
		get
		{
			if (_videoDialog == null)
			{
				_videoDialog = new FileDialog();
				AddChild(_videoDialog);
				ConfigureVideoFilters();
			}

			return _videoDialog;
		}
	}

	protected void OpenVideoFile()
	{
		VideoDialog.PopupCentered(new Vector2I(800, 600));
	}

	protected void OnVideoFileOpened(string inputPath)
	{
		InitMediaStream(inputPath);
	}

	protected void ConfigureVideoFilters()
	{
		// 1. Set the display mode to open files
		VideoDialog.FileMode = FileDialog.FileModeEnum.OpenFile;

		// 2. Clear any default filter configurations
		VideoDialog.ClearFilters();

		// 3. Append your explicit 3D asset filters
		// Format: "*.extension ; Human Readable Label"
		VideoDialog.AddFilter("*.mp4", "MPEG-4");
		VideoDialog.AddFilter("*.mkv", "Matroska");
		VideoDialog.AddFilter("*.mov", "QuickTime");

		// Optional: If you want both combined into a single selection dropdown slot:
		VideoDialog.AddFilter("*.mp4, *.mkv, *.mov ; Video Files");

		// 4. Set the access scope (FileSystem lets you browse the user's hard drive)
		VideoDialog.Access = FileDialog.AccessEnum.Filesystem;

		// 5. Connect the confirmation event handler delegate
		VideoDialog.FileSelected += OnVideoFileOpened;
	}
	#endregion

	#region Play Video
	[StructLayout(LayoutKind.Sequential)]
	public struct QueueDiagnostics
	{
		public int AudioPacketCount;
		public int VideoPacketCount;
		public int TotalAudioPacketCount;
		public int TotalVideoPacketCount;
		public UIntPtr AudioQueueBytes;
		public UIntPtr VideoQueueBytes;
		public UIntPtr PeakAudioQueueBytes;
		public UIntPtr PeakVideoQueueBytes;
		public UIntPtr PeakDecodedFrameBytes;
		public double TotalMemoryKB;
	}

	[StructLayout(LayoutKind.Sequential)]
	public struct MediaInfo
	{
		public int Width;
		public int Height;
		public double Fps;
		public double DurationSeconds;
		public int SampleRate;
		public int Channels;
		public int HasVideo;
		public int HasAudio;
	}

	private IntPtr _containerHandle = IntPtr.Zero;
	private MediaInfo _mediaInfo;

	// Video rendering buffers
	private byte[] _yBuffer;
	private byte[] _uBuffer;
	private byte[] _vBuffer;
	private Image _yImage, _uImage, _vImage;
	private ImageTexture _yTexture, _uTexture, _vTexture;
	private ShaderMaterial _shaderMaterial;

	private int _videoWidth;
	private int _videoHeight;

	// Video timing control
	private double _frameInterval = 0.0333; // Default ~30 FPS
	private double _timeAccumulator = 0.0;

	// Audio streaming buffers
	private float[] _audioBuffer;
	private AudioStreamGeneratorPlayback _audioPlayback;

	[DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
	private static extern QueueDiagnostics GetQueueDiagnostics(IntPtr container);

	[DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
	private static extern IntPtr OpenContainer(string filePath, ref MediaInfo outInfo);

	[DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
	private static extern int ReadNextVideoFrame(IntPtr container, byte[] outY, byte[] outU, byte[] outV);

	[DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
	private static extern int ReadNextAudioSamples(IntPtr container, float[] outFloatBuffer, int maxSamples);

	[DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
	private static extern void FreeContainer(IntPtr container);

	public void InitMediaStream(string filePath)
	{
		_mediaInfo = new MediaInfo();
		_containerHandle = OpenContainer(filePath, ref _mediaInfo);

		if (_containerHandle == IntPtr.Zero)
		{
			GD.PrintErr($"[FFmpeg] Failed to open container: {filePath}");
			return;
		}

		GD.Print($"[FFmpeg] Loaded: {filePath}");
		GD.Print($"[FFmpeg] Duration: {_mediaInfo.DurationSeconds:F2}s");

		// --- Setup Video (if present) ---
		if (_mediaInfo.HasVideo == 1)
		{
			_frameInterval = 1.0 / 30.0;
			_timeAccumulator = 0.0;

			GD.Print($"[FFmpeg] Video Stream: {_mediaInfo.Width}x{_mediaInfo.Height} @ {_mediaInfo.Fps:F2} FPS");

			InitializeYuvTextures(_mediaInfo.Width, _mediaInfo.Height);
		}

		// --- Setup Audio (if present) ---
		if (_mediaInfo.HasAudio == 1 && _audioPlayer != null)
		{
			GD.Print("[FFmpeg] Audio Stream Output: Standard 48000 Hz Stereo");

			// Allocate buffer for audio sample batches (2048 stereo sample pairs)
			_audioBuffer = new float[2048 * 2];

			// Create a fresh generator instance set to 48000 Hz
			AudioStreamGenerator generator = new AudioStreamGenerator();
			generator.MixRate = 48000;
			generator.BufferLength = 0.2f; // 200ms buffer capacity
			_audioPlayer.Stream = generator;

			// Start playback to spin up the audio device stream
			_audioPlayer.Play();
			_audioPlayback = _audioPlayer.GetStreamPlayback() as AudioStreamGeneratorPlayback;

			// PRE-BUFFER: Push initial samples BEFORE video playback begins
			if (_audioPlayback != null)
			{
				FillAudioBuffer();
			}
		}
	}

	public void InitializeYuvTextures(int width, int height, double fps = 30.0)
	{
		_videoWidth = width;
		_videoHeight = height;

		int chromaW = width / 2;
		int chromaH = height / 2;

		// Allocate memory buffers
		_yBuffer = new byte[width * height];
		_uBuffer = new byte[chromaW * chromaH];
		_vBuffer = new byte[chromaW * chromaH];

		// Create Planes
		_yPlane = YuvTexturePlane.Create(width, height);
		_uPlane = YuvTexturePlane.Create(chromaW, chromaH);
		_vPlane = YuvTexturePlane.Create(chromaW, chromaH);

		// Bind GPU textures to the Shader
		// --- Ensure we have a unique ShaderMaterial on the TextureRect ---
		if (_videoDisplay.Material is ShaderMaterial existingMat)
		{
			// Duplicate so runtime parameter changes bind to this specific instance
			_shaderMaterial = (ShaderMaterial)existingMat.Duplicate();
			_videoDisplay.Material = _shaderMaterial;
		}
		else
		{
			// Fallback: Create and assign ShaderMaterial if empty in Inspector
			var shader = GD.Load<Shader>("res://Shaders/yuv_shader.gdshader");
			_shaderMaterial = new ShaderMaterial { Shader = shader };
			_videoDisplay.Material = _shaderMaterial;
		}

		_shaderMaterial.SetShaderParameter("texture_y", _yPlane.Texture);
		_shaderMaterial.SetShaderParameter("texture_u", _uPlane.Texture);
		_shaderMaterial.SetShaderParameter("texture_v", _vPlane.Texture);

		// Give TextureRect a valid texture handle so Godot calculates node bounds
		_videoDisplay.Texture = _yPlane.Texture;
	}

	private void UploadTexturesToGpu()
	{
		// Y Plane: full resolution
		_yPlane.Update(_videoWidth, _videoHeight, _yBuffer);

		// U and V Planes: half resolution (chroma subsampled)
		int chromaW = _videoWidth / 2;
		int chromaH = _videoHeight / 2;

		_uPlane.Update(chromaW, chromaH, _uBuffer);
		_vPlane.Update(chromaW, chromaH, _vBuffer);
	}

	public override void _Process(double delta)
	{
		if (_containerHandle == IntPtr.Zero) return;

		// 1. Process Audio Stream First (Low-latency audio takes priority)
		if (_mediaInfo.HasAudio == 1)
		{
			FillAudioBuffer();
		}

		// 2. Process Video Frame Rendering (with timing accumulator circuit breaker)
		if (_mediaInfo.HasVideo == 1)
		{
			// Accumulate delta time from Godot's frame render loop
			_timeAccumulator += delta;

			// Check if enough time has passed to advance to the next video frame
			if (_timeAccumulator >= _frameInterval)
			{
				// Subtract interval instead of setting to 0 to preserve timing sub-frame accuracy
				_timeAccumulator -= _frameInterval;

				// Guard against large lag spikes/hangups accumulating many missed frames
				if (_timeAccumulator > _frameInterval * 2)
				{
					_timeAccumulator = 0.0;
				}

				RenderNextVideoFrame();
			}
		}
	}

	private bool RenderNextVideoFrame()
	{
		// 1. Fetch Y, U, V byte arrays directly from C++ DLL
		int result = ReadNextVideoFrame(_containerHandle, _yBuffer, _uBuffer, _vBuffer);

		if (result != 0)
		{
			SetProcess(false); // Stop processing on EOF
			PrintDiagnostics();
			return false;
		}

		// 2. Upload the updated byte arrays to GPU textures via our YuvTexturePlane structs
		UploadTexturesToGpu();

		return true;
	}

	private void PrintDiagnostics()
	{
		QueueDiagnostics diag = GetQueueDiagnostics(_containerHandle);

		// Print or display on an On-Screen Debug HUD:
		GD.Print($"Total Video Packets: {diag.TotalVideoPacketCount} packets");
		GD.Print($"Total Audio Packets: {diag.TotalAudioPacketCount} packets");
		GD.Print($"Peak Video Bytes: ({diag.PeakDecodedFrameBytes} Bytes)");
	}

	private void FillAudioBuffer()
	{
		if (_audioPlayback == null) return;

		int framesAvailable = _audioPlayback.GetFramesAvailable();
		if (framesAvailable <= 0) return;

		// Cap request to our internal buffer limit (2048 stereo frames)
		int framesToRead = Math.Min(framesAvailable, 2048);

		// Read stereo frames from C++ DLL
		int framesRead = ReadNextAudioSamples(_containerHandle, _audioBuffer, framesToRead);

		for (int i = 0; i < framesRead; i++)
		{
			float leftChannel = Math.Clamp(_audioBuffer[i * 2], -1.0f, 1.0f);
			float rightChannel = Math.Clamp(_audioBuffer[i * 2 + 1], -1.0f, 1.0f);

			_audioPlayback.PushFrame(new Vector2(leftChannel, rightChannel));
		}
	}

	public override void _ExitTree()
	{
		if (_containerHandle != IntPtr.Zero)
		{
			FreeContainer(_containerHandle);
			_containerHandle = IntPtr.Zero;
			GD.Print("[FFmpeg] Container handle freed cleanly.");
		}
	}
	#endregion
}
