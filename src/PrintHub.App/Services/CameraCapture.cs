using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Devices.Enumeration;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Media.Playback;
using Windows.Storage.Streams;

namespace PrintHub.App.Services;

/// <summary>Capture a photo of a document with a webcam or laptop camera (live preview in a dialog).</summary>
public static class CameraCapture
{
    public sealed record Result(byte[] Jpeg, bool CleanUp);

    public static async Task<Result?> CaptureAsync(XamlRoot root)
    {
        var cameras = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture);
        if (cameras.Count == 0)
        {
            await Ui.MessageAsync(root, "No camera found", "Connect a webcam, or make sure the camera isn't disabled in Device Manager.");
            return null;
        }

        MediaCapture? capture = null;
        MediaPlayer? player = null;
        var preview = new MediaPlayerElement { Width = 640, Height = 360, Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform };
        var status = new TextBlock { Opacity = 0.7, TextWrapping = TextWrapping.Wrap, Text = "Starting the camera…" };
        var picker = new ComboBox { Header = "Camera", HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var c in cameras) picker.Items.Add(new ComboBoxItem { Content = c.Name, Tag = c.Id });
        picker.SelectedIndex = 0;
        var cleanUp = new CheckBox { Content = "Clean up as a document (crop, straighten, whiten paper)", IsChecked = true };

        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(picker); panel.Children.Add(preview); panel.Children.Add(status); panel.Children.Add(cleanUp);

        var dialog = new ContentDialog
        {
            Title = "Scan with camera", Content = panel, PrimaryButtonText = "Capture", CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary, XamlRoot = root, IsPrimaryButtonEnabled = false,
        };

        async Task StartAsync(string id)
        {
            Stop();
            status.Text = "Starting the camera…"; dialog.IsPrimaryButtonEnabled = false;
            try
            {
                capture = new MediaCapture();
                await capture.InitializeAsync(new MediaCaptureInitializationSettings { VideoDeviceId = id, StreamingCaptureMode = StreamingCaptureMode.Video, MemoryPreference = MediaCaptureMemoryPreference.Auto });
                var source = capture.FrameSources.Values.FirstOrDefault(s => s.Info.SourceKind == MediaFrameSourceKind.Color && s.Info.MediaStreamType == MediaStreamType.VideoPreview)
                             ?? capture.FrameSources.Values.FirstOrDefault(s => s.Info.SourceKind == MediaFrameSourceKind.Color);
                if (source is null) throw new InvalidOperationException("This camera has no colour video stream.");
                player = new MediaPlayer { Source = MediaSource.CreateFromMediaFrameSource(source), RealTimePlayback = true };
                preview.SetMediaPlayer(player);
                player.Play();
                status.Text = "Hold the page flat in view with good light, then press Capture.";
                dialog.IsPrimaryButtonEnabled = true;
            }
            catch (UnauthorizedAccessException)
            {
                status.Text = "Windows blocked camera access. Open Settings > Privacy & security > Camera and allow desktop apps to use the camera.";
            }
            catch (Exception ex) { status.Text = "Couldn't start the camera: " + ex.Message; }
        }

        void Stop()
        {
            try { preview.SetMediaPlayer(null); player?.Dispose(); } catch { }
            try { capture?.Dispose(); } catch { }
            player = null; capture = null;
        }

        picker.SelectionChanged += async (_, _) => { if (picker.SelectedItem is ComboBoxItem { Tag: string id }) await StartAsync(id); };
        byte[]? photo = null;
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            var deferral = args.GetDeferral();
            try
            {
                using var ras = new InMemoryRandomAccessStream();
                await capture!.CapturePhotoToStreamAsync(ImageEncodingProperties.CreateJpeg(), ras);
                ras.Seek(0);
                var buf = new byte[ras.Size];
                await ras.ReadAsync(buf.AsBuffer(), (uint)ras.Size, InputStreamOptions.None);
                photo = buf;
            }
            catch (Exception ex) { status.Text = "Capture failed: " + ex.Message; args.Cancel = true; }
            finally { deferral.Complete(); }
        };

        _ = StartAsync((string)((ComboBoxItem)picker.SelectedItem).Tag);
        try
        {
            var r = await dialog.ShowAsync();
            return r == ContentDialogResult.Primary && photo is not null ? new Result(photo, cleanUp.IsChecked == true) : null;
        }
        finally { Stop(); }
    }
}
