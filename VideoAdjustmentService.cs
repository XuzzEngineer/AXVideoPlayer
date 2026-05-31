using System;
using LibVLCSharp.Shared;

namespace AXVideoPlayer
{
    internal sealed class VideoAdjustmentService
    {
        private readonly MediaPlayer _mediaPlayer;

        public bool IsEnabled { get; private set; }
        public float Brightness { get; private set; } = 1.0f;
        public float Contrast { get; private set; } = 1.0f;
        public float Saturation { get; private set; } = 1.0f;
        public float Gamma { get; private set; } = 1.0f;
        public float Hue { get; private set; } = 0.0f;
        public float Sharpness { get; private set; } = 0.0f;

        public VideoAdjustmentService(MediaPlayer mediaPlayer)
        {
            _mediaPlayer = mediaPlayer ?? throw new ArgumentNullException(nameof(mediaPlayer));
        }

        public void SetEnabled(bool enabled)
        {
            IsEnabled = enabled;
            Apply();
        }

        public void SetBrightness(float value) { Brightness = Clamp(value, 0f, 2f); Apply(); }
        public void SetContrast(float value) { Contrast = Clamp(value, 0f, 2f); Apply(); }
        public void SetSaturation(float value) { Saturation = Clamp(value, 0f, 3f); Apply(); }
        public void SetGamma(float value) { Gamma = Clamp(value, 0.1f, 3f); Apply(); }
        public void SetHue(float value) { Hue = Clamp(value, -180f, 180f); Apply(); }

        // VLC exposes brightness/contrast/saturation/hue/gamma through video adjustment.
        // Sharpness is kept modular here so the UI/API is ready; applying it requires VLC's sharpen filter support.
        public void SetSharpness(float value) { Sharpness = Clamp(value, 0f, 2f); }

        public void Reset()
        {
            Brightness = 1.0f;
            Contrast = 1.0f;
            Saturation = 1.0f;
            Gamma = 1.0f;
            Hue = 0.0f;
            Sharpness = 0.0f;
            Apply();
        }

        public void Apply()
        {
            try
            {
                _mediaPlayer.SetAdjustInt(VideoAdjustOption.Enable, IsEnabled ? 1 : 0);

                if (!IsEnabled)
                    return;

                _mediaPlayer.SetAdjustFloat(VideoAdjustOption.Contrast, Contrast);
                _mediaPlayer.SetAdjustFloat(VideoAdjustOption.Brightness, Brightness);
                _mediaPlayer.SetAdjustFloat(VideoAdjustOption.Hue, Hue);
                _mediaPlayer.SetAdjustFloat(VideoAdjustOption.Saturation, Saturation);
                _mediaPlayer.SetAdjustFloat(VideoAdjustOption.Gamma, Gamma);
            }
            catch
            {
                // Keep playback working even if this VLC build rejects video adjustment.
            }
        }

        private static float Clamp(float value, float min, float max) => Math.Max(min, Math.Min(max, value));
    }
}
