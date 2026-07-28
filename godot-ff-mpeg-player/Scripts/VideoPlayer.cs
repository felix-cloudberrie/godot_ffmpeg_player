using Godot;
using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

public partial class VideoPlayer : Control
{
	private const string DllName = "FFMpegInterface";

	[Export]
	private TextureRect _videoDisplay;

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

	public override void _Process(double delta)
	{
		RenderNextVideoFrame(delta);
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
		InitVideoStream(inputPath);
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

	#region Test FillArray

	[DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
	private static extern void FillArray([In, Out] int[] array, int size);

	[DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
	private static extern int OpenVideoFile(string filePath, ref VideoInfo outInfo);

	private void PrintArray(int[] array)
	{
		for (int i = 0; i < array.Length; i++)
		{
			GD.Print($"Index {i}: {array[i]}");
		}
	}
	#endregion

	#region Play Video
	private IntPtr _decoderHandle = IntPtr.Zero;
	private byte[] _frameBuffer;
	private Image _godotImage;
	private ImageTexture _godotTexture;
	private int _width;
	private int _height;

	public double TargetFps = 30.0;

	private double _frameInterval;
	private double _timeAccumulator = 0.0;

	[DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
	private static extern IntPtr CreateDecoder(string filePath, out int width, out int height, out double fps);

	[DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
	private static extern int ReadNextFrame(IntPtr decoder, byte[] outRgbaBuffer);

	[DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
	private static extern void FreeDecoder(IntPtr decoder);

	public void RenderNextVideoFrame(double delta)
	{
		if (_decoderHandle == IntPtr.Zero) return;

		// Accumulate time passed
		_timeAccumulator += delta;

		// Preventive Measure: If accumulator gets way too far ahead (e.g., > 0.2s after window focus/lag),
		// clamp it so it doesn't trigger back-to-back frame decodes to catch up.
		if (_timeAccumulator > 0.2)
		{
			_timeAccumulator = _frameInterval;
		}

		// Process frames while we are behind schedule
		if (_timeAccumulator >= _frameInterval)
		{
			_timeAccumulator -= _frameInterval;

			int result = ReadNextFrame(_decoderHandle, _frameBuffer);

			if (result == 0)
			{
				_godotImage.SetData(_width, _height, false, Image.Format.Rgba8, _frameBuffer);
				_godotTexture.Update(_godotImage);
			}
			else if (result == 1)
			{
				GD.Print("End of video reached.");
				SetProcess(false);
			}
		}
	}

	public override void _ExitTree()
	{
		if (_decoderHandle != IntPtr.Zero)
		{
			FreeDecoder(_decoderHandle);
			_decoderHandle = IntPtr.Zero;
			GD.Print("Native decoder freed cleanly.");
		}
	}

	public void InitVideoStream(string inputPath)
	{
		double realFps;
		_decoderHandle = CreateDecoder(inputPath, out _width, out _height, out realFps);

		if (_decoderHandle != IntPtr.Zero)
		{
			TargetFps = realFps > 0 ? realFps : 30.0;
			_frameInterval = 1.0 / TargetFps;
			_timeAccumulator = 0.0; // Ensure accumulator starts fresh at 0

			GD.Print($"Decoder initialized: {_width}x{_height} @ {TargetFps:F2} FPS");

			_frameBuffer = new byte[_width * _height * 4];
			_godotImage = Image.CreateEmpty(_width, _height, false, Image.Format.Rgba8);
			_godotTexture = ImageTexture.CreateFromImage(_godotImage);
			_videoDisplay.Texture = _godotTexture;
		}
	}
	#endregion

	#region Open Video
	// Layout must match C++ VideoInfo struct
	[StructLayout(LayoutKind.Sequential)]
	public struct VideoInfo
	{
		public int Width;
		public int Height;
		public double DurationSeconds;
		public int HasAudio;
	}

	public void RetrieveVideoInfo(string inputPath)
	{
		VideoInfo info = new VideoInfo();

		int result = OpenVideoFile(inputPath, ref info);

		if (result == 0)
		{
			GD.Print($"[FFmpeg] Resolution: {info.Width}x{info.Height}");
			GD.Print($"[FFmpeg] Duration: {info.DurationSeconds:F2}s");
			GD.Print($"[FFmpeg] Audio Stream Present: {(info.HasAudio == 1 ? "Yes" : "No")}");
		}
		else
		{
			GD.PrintErr($"OpenVideoFile failed with error code: {result}");
		}
	}
	#endregion
}
