using Godot;
using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;

public partial class VideoPlayer : Control
{
	private const string DllName = "FFMpegInterface";

	[Export] private TextureRect _videoDisplay;
	[Export] private AudioStreamPlayer _audioPlayer;

	// --- Interop Structs ---
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

	// --- Decoded YUV Frame Container ---
	public struct YuvFrame
	{
		public byte[] Y;
		public byte[] U;
		public byte[] V;

		public YuvFrame(int width, int height)
		{
			int chromaW = width / 2;
			int chromaH = height / 2;
			Y = new byte[width * height];
			U = new byte[chromaW * chromaH];
			V = new byte[chromaW * chromaH];
		}
	}

	// --- YUV Texture Plane Wrapper ---
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

	// --- Native DLL Exports ---
	[DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
	private static extern IntPtr OpenContainer(string filePath, ref MediaInfo outInfo);

	[DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
	private static extern int ReadNextVideoFrame(IntPtr container, byte[] outY, byte[] outU, byte[] outV);

	[DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
	private static extern int ReadNextAudioSamples(IntPtr container, float[] outFloatBuffer, int maxSamples);

	[DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
	private static extern QueueDiagnostics GetQueueDiagnostics(IntPtr container);

	[DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
	private static extern void FreeContainer(IntPtr container);

	static VideoPlayer()
	{
		NativeLibrary.SetDllImportResolver(typeof(VideoPlayer).Assembly, ResolveNativeLibrary);
	}

	private static IntPtr ResolveNativeLibrary(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
	{
		if (libraryName == DllName)
		{
			string relativePath = Path.Combine("native", "x64", $"{libraryName}.dll");
			string fullPath = Path.Combine(Directory.GetCurrentDirectory(), relativePath);
			if (File.Exists(fullPath)) return NativeLibrary.Load(fullPath);
		}
		return IntPtr.Zero;
	}

	// --- VIDEO SYNCHRONIZATION ---
	private readonly object _videoLock = new object();
	private readonly AutoResetEvent _videoBufferEvent = new AutoResetEvent(false);
	private CircularBuffer<YuvFrame> _videoBuffer;

	// --- AUDIO SYNCHRONIZATION ---
	private readonly object _audioLock = new object();
	private readonly AutoResetEvent _audioBufferEvent = new AutoResetEvent(false);
	private CircularBuffer<float[]> _audioBuffer;

	// Background decoding thread
	private Thread _workerThread;
	private volatile bool _isWorkerRunning = false;

	// FFmpeg state
	private IntPtr _containerHandle = IntPtr.Zero;
	private MediaInfo _mediaInfo;

	// Rendering & Shader State
	private YuvTexturePlane _yPlane, _uPlane, _vPlane;
	private ShaderMaterial _shaderMaterial;
	private int _videoWidth;
	private int _videoHeight;

	// Audio Playback State
	private AudioStreamGeneratorPlayback _audioPlayback;
	private const int AudioChunkFrames = 2048; // 2048 stereo frame pairs per buffer unit

	// Timing Control
	private double _frameInterval = 1.0 / 30.0;
	private double _timeAccumulator = 0.0;

	public bool IsPlaying { get; private set; } = false;
	private bool _isLastFrameRetrieved = false;

	#region File Dialog
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

	public override void _Input(InputEvent @event)
	{
		if (@event is InputEventKey eventKey && eventKey.Pressed)
		{
			if (eventKey.Keycode == Key.O)
			{
				GD.Print("Open Video File...");
				OpenVideoFile();
			}
			else if (eventKey.Keycode == Key.P)
			{
				TogglePlay();
			}
		}
	}

	public void TogglePlay()
	{
		IsPlaying = !IsPlaying;

		if (_audioPlayer != null)
		{
			_audioPlayer.StreamPaused = !IsPlaying;

			// Ensure stream is active if resuming from a paused state
			if (IsPlaying && !_audioPlayer.Playing)
			{
				_audioPlayer.Play();
			}
		}

		GD.Print($"[VideoPlayer] Playback State: {(IsPlaying ? "PLAYING" : "PAUSED")}");
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

	public void InitMediaStream(string filePath)
	{
		// Stop any running thread before re-initializing
		StopWorkerThread();

		_mediaInfo = new MediaInfo();
		_containerHandle = OpenContainer(filePath, ref _mediaInfo);

		if (_containerHandle == IntPtr.Zero)
		{
			GD.PrintErr($"[FFmpeg] Failed to open container: {filePath}");
			return;
		}

		GD.Print($"[FFmpeg] Loaded: {filePath}");
		GD.Print($"Duration: {_mediaInfo.DurationSeconds:F2}s");
		GD.Print($"Resolution: {_mediaInfo.Width} x {_mediaInfo.Height}");

		if (_mediaInfo.HasVideo == 1)
		{
			lock (_videoLock)
			{
				// 500ms at 30 FPS = 15 Frames
				_videoBuffer = new CircularBuffer<YuvFrame>(15);
				InitializeYuvTextures(_mediaInfo.Width, _mediaInfo.Height);
			}

			// Render first frame to signal successful load
			if (_mediaInfo.HasVideo == 0 || _containerHandle == IntPtr.Zero) return;

			YuvFrame firstFrame = new YuvFrame(_videoWidth, _videoHeight);
			int result = ReadNextVideoFrame(_containerHandle, firstFrame.Y, firstFrame.U, firstFrame.V);

			if (result == 0)
			{
				RenderFrame(firstFrame);
			}
		}

		if (_mediaInfo.HasAudio == 1 && _audioPlayer != null)
		{
			lock (_audioLock)
			{
				// 500ms at 48kHz Stereo (~24,000 samples). 
				// Each chunk holds 2048 stereo frames (~42.6ms), so 12 chunks = ~500ms.
				_audioBuffer = new CircularBuffer<float[]>(12);
				SetupAudioPlayer();
			}
		}

		// Set initial playback state (e.g. paused on load)
		IsPlaying = false;
		if (_audioPlayer != null)
		{
			_audioPlayer.StreamPaused = true;
		}

		_isWorkerRunning = true;
		_workerThread = new Thread(WorkerThreadLoop)
		{
			Name = "FFmpeg_Producer_Thread",
			IsBackground = true
		};
		_workerThread.Start();
	}

	private void InitializeYuvTextures(int width, int height)
	{
		_videoWidth = width;
		_videoHeight = height;

		int chromaW = width / 2;
		int chromaH = height / 2;

		_yPlane = YuvTexturePlane.Create(width, height);
		_uPlane = YuvTexturePlane.Create(chromaW, chromaH);
		_vPlane = YuvTexturePlane.Create(chromaW, chromaH);

		if (_videoDisplay.Material is ShaderMaterial existingMat)
		{
			_shaderMaterial = (ShaderMaterial)existingMat.Duplicate();
			_videoDisplay.Material = _shaderMaterial;
		}
		else
		{
			var shader = GD.Load<Shader>("res://Shaders/yuv_shader.gdshader");
			_shaderMaterial = new ShaderMaterial { Shader = shader };
			_videoDisplay.Material = _shaderMaterial;
		}

		_shaderMaterial.SetShaderParameter("texture_y", _yPlane.Texture);
		_shaderMaterial.SetShaderParameter("texture_u", _uPlane.Texture);
		_shaderMaterial.SetShaderParameter("texture_v", _vPlane.Texture);

		_videoDisplay.Texture = _yPlane.Texture;
	}

	private void SetupAudioPlayer()
	{
		AudioStreamGenerator generator = new AudioStreamGenerator();
		generator.MixRate = 48000;
		generator.BufferLength = 0.2f; // 200ms device buffer capacity
		_audioPlayer.Stream = generator;
		_audioPlayer.Play();
		_audioPlayback = _audioPlayer.GetStreamPlayback() as AudioStreamGeneratorPlayback;
	}

	private void WorkerThreadLoop()
	{
		// Wait handle array so the thread can sleep until EITHER buffer needs filling
		WaitHandle[] waitHandles = new WaitHandle[] { _videoBufferEvent, _audioBufferEvent };

		while (_isWorkerRunning)
		{
			bool needMoreVideo = false;
			bool needMoreAudio = false;

			// Check Video Capacity
			lock (_videoLock)
			{
				if (_videoBuffer != null && !_videoBuffer.IsFull())
					needMoreVideo = true;
			}

			// Check Audio Capacity
			lock (_audioLock)
			{
				if (_audioBuffer != null && !_audioBuffer.IsFull())
					needMoreAudio = true;
			}

			// If BOTH buffers are full, sleep until consumer signals one of them
			if (!needMoreVideo && !needMoreAudio)
			{
				WaitHandle.WaitAny(waitHandles, 10); // Sleep until signaled or 10ms timeout
				continue;
			}

			// --- DECODE VIDEO IF NEEDED ---
			if (needMoreVideo)
			{
				YuvFrame frame = new YuvFrame(_videoWidth, _videoHeight);
				int result = ReadNextVideoFrame(_containerHandle, frame.Y, frame.U, frame.V);

				if (result == 0)
				{
					lock (_videoLock)
					{
						_videoBuffer.Push(frame);
					}
				}
				else if (result == 1) // EOF
				{
					_isWorkerRunning = false;
					PrintDiagnostics();
					break;
				}
			}

			// --- DECODE AUDIO IF NEEDED ---
			if (needMoreAudio)
			{
				float[] audioChunk = new float[AudioChunkFrames * 2];
				int framesRead = ReadNextAudioSamples(_containerHandle, audioChunk, AudioChunkFrames);

				if (framesRead > 0)
				{
					lock (_audioLock)
					{
						_audioBuffer.Push(audioChunk);
					}
				}
			}
		}
	}

	public override void _Process(double delta)
	{
		if (_containerHandle == IntPtr.Zero || !IsPlaying) return;

		// 1. Audio Processing Priority
		if (_mediaInfo.HasAudio == 1)
		{
			FillAudioBuffer();
		}

		// 2. Video Rendering Synchronization
		if (_mediaInfo.HasVideo == 1)
		{
			_timeAccumulator += delta;

			if (_timeAccumulator >= _frameInterval)
			{
				_timeAccumulator -= _frameInterval;

				// Circuit breaker for large frame drops
				if (_timeAccumulator > _frameInterval * 2)
				{
					_timeAccumulator = 0.0;
				}

				RenderNextVideoFrame();
			}
		}
	}

	private void RenderNextVideoFrame()
	{
		YuvFrame frameToRender = default;
		bool frameRetrieved = false;

		lock (_videoLock)
		{
			if (_videoBuffer != null && !_videoBuffer.IsEmpty())
			{
				frameToRender = _videoBuffer.Pop();
				frameRetrieved = true;

				// Signal worker thread that VIDEO buffer has space
				_videoBufferEvent.Set();
			}
		}

		if (frameRetrieved)
		{
			RenderFrame(frameToRender);
		}
	}

	private void RenderFrame(YuvFrame frameToRender)
	{
		int chromaW = _videoWidth / 2;
		int chromaH = _videoHeight / 2;
		_yPlane.Update(_videoWidth, _videoHeight, frameToRender.Y);
		_uPlane.Update(chromaW, chromaH, frameToRender.U);
		_vPlane.Update(chromaW, chromaH, frameToRender.V);
	}

	private void FillAudioBuffer()
	{
		if (_audioPlayback == null) return;

		int framesAvailable = _audioPlayback.GetFramesAvailable();
		if (framesAvailable < AudioChunkFrames) return;

		float[] audioChunk = null;

		lock (_audioLock)
		{
			if (_audioBuffer != null && !_audioBuffer.IsEmpty())
			{
				audioChunk = _audioBuffer.Pop();

				// Signal worker thread that AUDIO buffer has space
				_audioBufferEvent.Set();
			}
		}

		if (audioChunk != null)
		{
			for (int i = 0; i < AudioChunkFrames; i++)
			{
				float left = Math.Clamp(audioChunk[i * 2], -1.0f, 1.0f);
				float right = Math.Clamp(audioChunk[i * 2 + 1], -1.0f, 1.0f);
				_audioPlayback.PushFrame(new Vector2(left, right));
			}
		}
	}

	private void StopWorkerThread()
	{
		_isWorkerRunning = false;
		_videoBufferEvent.Set(); // Wake up worker if waiting on video
		_audioBufferEvent.Set(); // Wake up worker if waiting on audio

		if (_workerThread != null && _workerThread.IsAlive)
		{
			_workerThread.Join(500);
			_workerThread = null;
		}
	}

	private void PrintDiagnostics()
	{
		QueueDiagnostics diag = GetQueueDiagnostics(_containerHandle);

		// Print or display on an On-Screen Debug HUD:
		GD.Print($"Total Video Packets: {diag.TotalVideoPacketCount} packets");
		GD.Print($"Total Audio Packets: {diag.TotalAudioPacketCount} packets");
		GD.Print($"Peak Video Bytes: ({diag.PeakDecodedFrameBytes} Bytes)");
	}


	public override void _ExitTree()
	{
		StopWorkerThread();

		if (_containerHandle != IntPtr.Zero)
		{
			FreeContainer(_containerHandle);
			_containerHandle = IntPtr.Zero;
			GD.Print("[FFmpeg] Container handle freed cleanly.");
		}
	}
}