using System;
using LibVLCSharp.Shared;

namespace AXVideoPlayer
{
    internal sealed class AudioEqualizerService
    {
        private readonly MediaPlayer _mediaPlayer;
        private Equalizer? _equalizer;

        private float _preamp;
        private readonly float[] _bands = new float[10];

        public bool IsEnabled { get; private set; }

        public AudioEqualizerService(MediaPlayer mediaPlayer)
        {
            _mediaPlayer = mediaPlayer ?? throw new ArgumentNullException(nameof(mediaPlayer));
        }

        public void SetEnabled(bool enabled)
        {
            IsEnabled = enabled;
            Apply();
        }

        public void SetPreamp(float db)
        {
            _preamp = Clamp(db, -20f, 20f);
            Apply();
        }

        public void SetBand(int bandIndex, float db)
        {
            if (bandIndex < 0 || bandIndex >= _bands.Length)
                return;

            _bands[bandIndex] = Clamp(db, -20f, 20f);
            Apply();
        }

        public void Reset()
        {
            _preamp = 0f;
            for (int i = 0; i < _bands.Length; i++)
                _bands[i] = 0f;
            Apply();
        }

        public void Apply()
        {
            if (!IsEnabled)
            {
                TrySetEqualizer(null);
                return;
            }

            _equalizer ??= new Equalizer();
            if (_equalizer == null)
                return;

            _equalizer.SetPreamp(_preamp);

            for (int i = 0; i < _bands.Length; i++)
                _equalizer.SetAmp(_bands[i], (uint)i);

            TrySetEqualizer(_equalizer);
        }

        private void TrySetEqualizer(Equalizer? equalizer)
        {
            try
            {
                if (equalizer == null)
                    _mediaPlayer.UnsetEqualizer();
                else
                    _mediaPlayer.SetEqualizer(equalizer);
            }
            catch
            {
                // Keep playback working even if this VLC build rejects equalizer control.
            }
        }

        private static float Clamp(float value, float min, float max) => Math.Max(min, Math.Min(max, value));
    }
}
