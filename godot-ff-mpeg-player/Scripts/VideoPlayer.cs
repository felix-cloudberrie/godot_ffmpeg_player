using Godot;
using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using static VideoPlayer;

public partial class VideoPlayer : Control
{
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
		public UIntPtr AudioQueueBytes;
		public UIntPtr VideoQueueBytes;
		public UIntPtr PeakAudioQueueBytes;
		public UIntPtr PeakVideoQueueBytes;
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
	private byte[] _videoFrameBuffer;
	private Image _godotImage;
	private ImageTexture _godotTexture;

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
	private static extern int ReadNextVideoFrame(IntPtr container, byte[] outRgbaBuffer);

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
			_frameInterval = 1.0 / (_mediaInfo.Fps > 0 ? _mediaInfo.Fps : 30.0);
			_timeAccumulator = 0.0;

			GD.Print($"[FFmpeg] Video Stream: {_mediaInfo.Width}x{_mediaInfo.Height} @ {_mediaInfo.Fps:F2} FPS");

			// Allocate RGBA pixel buffer (4 bytes per pixel)
			_videoFrameBuffer = new byte[_mediaInfo.Width * _mediaInfo.Height * 4];

			// Initialize Godot Image and TextureRect
			_godotImage = Image.CreateEmpty(_mediaInfo.Width, _mediaInfo.Height, false, Image.Format.Rgba8);
			_godotTexture = ImageTexture.CreateFromImage(_godotImage);
			_videoDisplay.Texture = _godotTexture;
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
			RenderNextVideoFrame(delta);
		}

		if (_containerHandle != IntPtr.Zero)
		{
			QueueDiagnostics diag = GetQueueDiagnostics(_containerHandle);

			// Print or display on an On-Screen Debug HUD:
			GD.Print($"Video Queue: {diag.VideoPacketCount} pkts ({diag.VideoQueueBytes.ToUInt64() / 1024} KB)");
			GD.Print($"Audio Queue: {diag.AudioPacketCount} pkts ({diag.AudioQueueBytes.ToUInt64() / 1024} KB)");
			GD.Print($"Peak Video Bytes: ({diag.PeakVideoQueueBytes / 1024} KB)");
			GD.Print($"Peak Audio Bytes: ({diag.PeakAudioQueueBytes / 1024} KB)");
			GD.Print($"Total RAM: {diag.TotalMemoryKB:F2} KB");
		}
	}

	private void RenderNextVideoFrame(double delta)
	{
		_timeAccumulator += delta;

		// Circuit Breaker: Cap accumulator at 0.2s to prevent catch-up speedups during lag spikes
		if (_timeAccumulator > 0.2)
		{
			_timeAccumulator = _frameInterval;
		}

		if (_timeAccumulator >= _frameInterval)
		{
			_timeAccumulator -= _frameInterval;

			int result = ReadNextVideoFrame(_containerHandle, _videoFrameBuffer);

			if (result == 0)
			{
				_godotImage.SetData(_mediaInfo.Width, _mediaInfo.Height, false, Image.Format.Rgba8, _videoFrameBuffer);
				_godotTexture.Update(_godotImage);
			}
			else if (result == 1)
			{
				GD.Print("[FFmpeg] End of video stream reached.");
				SetProcess(false); // Stop processing on EOF
			}
		}
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
