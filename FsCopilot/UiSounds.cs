namespace FsCopilot;

using System.Reflection;
using System.Media;

public static class UiSounds
{
    public static void Play(string name)
    {
        var asm = Assembly.GetExecutingAssembly();
        using Stream? stream = asm.GetManifestResourceStream($"FsCopilot.Assets.{name}.wav");
        if (stream == null) return;

        using var player = new SoundPlayer(stream);
        player.Play();
    }
}