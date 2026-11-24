using System.Runtime.InteropServices.WindowsRuntime;
using DccwPlus.Core.Calibration;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace DccwPlus.App;

public sealed class PatternSurface : Grid
{
    private readonly Image _image = new() { Stretch = Stretch.Fill, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly DispatcherQueueTimer _render;
    private XamlRoot? _root;
    private ReferencePatternKind _kind;
    private GammaExample _example;
    private (ReferencePatternKind Kind, GammaExample Example, int Width, int Height, double Scale)? _last;

    public PatternSurface()
    {
        Background = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 128, 128, 128));
        UseLayoutRounding = true;
        Children.Add(_image);
        _render = DispatcherQueue.CreateTimer();
        _render.IsRepeating = false;
        _render.Interval = TimeSpan.FromMilliseconds(70);
        _render.Tick += (_, _) => Render();
        SizeChanged += (_, _) => ScheduleRender();
        Loaded += (_, _) =>
        {
            _root = XamlRoot;
            if (_root is not null) _root.Changed += RootChanged;
            ScheduleRender();
        };
        Unloaded += (_, _) =>
        {
            if (_root is not null) _root.Changed -= RootChanged;
            _root = null;
            _render.Stop();
            _image.Source = null;
            _last = null;
        };
    }

    public void SetPattern(ReferencePatternKind kind, GammaExample example = GammaExample.Live)
    {
        _kind = kind;
        _example = example;
        ScheduleRender();
    }

    private void RootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => ScheduleRender();

    private void ScheduleRender()
    {
        if (_root is null) return;
        _render.Stop();
        _render.Start();
    }

    private void Render()
    {
        if (_root is null || ActualWidth < 1 || ActualHeight < 1) return;
        double scale = _root.RasterizationScale;
        int width = Math.Clamp((int)Math.Floor(ActualWidth * scale), 1, 4096);
        int height = Math.Clamp((int)Math.Floor(ActualHeight * scale), 1, 2160);
        var key = (_kind, _example, width, height, scale);
        if (_last == key) return;
        var bitmap = new WriteableBitmap(width, height);
        using (var stream = bitmap.PixelBuffer.AsStream())
            stream.Write(ReferencePattern.Draw(_kind, width, height, _example));
        bitmap.Invalidate();
        _image.Width = width / scale;
        _image.Height = height / scale;
        _image.Source = bitmap;
        _last = key;
    }
}
