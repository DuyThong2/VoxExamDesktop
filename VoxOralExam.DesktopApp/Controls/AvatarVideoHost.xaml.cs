using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace VoxOralExam.DesktopApp.Controls;

/// <summary>
/// Lightweight WPF control hosting the avatar for the whole exam (Phase 5 of
/// docs/realtime-self-hosted-avatar-plan.md). No Tavus host to coexist with anymore -- it was
/// deleted in Phase 1. ExamViewModel sets VideoFrame from AvatarWebRtcClient.OnVideoFrame and
/// IsSpeaking from AvatarWebRtcClient.OnSpeakingChanged, the same BitmapImage-property-binding
/// pattern CameraService/CameraPreview already use.
/// </summary>
public partial class AvatarVideoHost : UserControl
{
    public static readonly DependencyProperty VideoFrameProperty = DependencyProperty.Register(
        nameof(VideoFrame), typeof(BitmapImage), typeof(AvatarVideoHost));

    public static readonly DependencyProperty IsSpeakingProperty = DependencyProperty.Register(
        nameof(IsSpeaking), typeof(bool), typeof(AvatarVideoHost),
        new PropertyMetadata(false, OnIsSpeakingChanged));

    public AvatarVideoHost()
    {
        InitializeComponent();
    }

    public BitmapImage? VideoFrame
    {
        get => (BitmapImage?)GetValue(VideoFrameProperty);
        set => SetValue(VideoFrameProperty, value);
    }

    public bool IsSpeaking
    {
        get => (bool)GetValue(IsSpeakingProperty);
        set => SetValue(IsSpeakingProperty, value);
    }

    private static void OnIsSpeakingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is AvatarVideoHost host)
        {
            host.UpdateRippleAnimation((bool)e.NewValue);
        }
    }

    private void UpdateRippleAnimation(bool isSpeaking)
    {
        var storyboard = (Storyboard)Resources["RippleStoryboard"];
        if (isSpeaking)
        {
            storyboard.Begin(this, true);
        }
        else
        {
            storyboard.Stop(this);
            Ripple1.Opacity = 0;
            Ripple2.Opacity = 0;
            Ripple3.Opacity = 0;
        }
    }
}
