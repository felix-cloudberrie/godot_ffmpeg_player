using Godot;

public partial class VideoBoard : Node
{
	[Export]
	private VideoPlayer[] _videoPlayers;

	private int _videoPlayerCount = 0;

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
				if (_videoPlayerCount == _videoPlayers.Length)
				{
					GD.Print("Grid is full!");
					return;
				}

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
		for (int i = 0; i < _videoPlayerCount; i++)
		{
			_videoPlayers[i].TogglePlay();
		}
	}

	protected void OpenVideoFile()
	{
		VideoDialog.PopupCentered(new Vector2I(800, 600));
	}

	protected void OnVideoFileOpened(string inputPath)
	{
		VideoPlayer videoPlayer = _videoPlayers[_videoPlayerCount++];
		videoPlayer.InitMediaStream(inputPath);
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
}
