using System.IO;
using System.Media;
using BusPulse.Configuration;

namespace BusPulse.Services;

public sealed class SoundService(AppConfig config)
{
    private static readonly string SoundsDirectory = Path.Combine(AppContext.BaseDirectory, "Assets", "Sounds");

    public void PlayArrival() => Play("arrival.wav");

    public void PlayDelay() => Play("delay.wav");

    private void Play(string fileName)
    {
        if (config.NotificationsMuted)
        {
            return;
        }

        var path = Path.Combine(SoundsDirectory, fileName);
        if (!File.Exists(path))
        {
            return;
        }

        using var player = new SoundPlayer(path);
        player.Play();
    }
}
