using System;
using System.Collections.Generic;
using System.Linq;

namespace Notch.Services;

public enum SonarScene { Idle, Music, Game, Meeting }

/// <summary>Otomatik profil gecisinin kurallari.</summary>
public static class ProfileRules
{
    /// <summary>Virgulle ayrilmis uygulama adlari ("Zoom, teams.exe") -> surec adlari.</summary>
    public static HashSet<string> Names(string list) =>
        list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(n => n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? n[..^4] : n)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Oncelik: gorusme &gt; oyun &gt; muzik &gt; bosta. Profil atanmamis bir durum atlanir,
    /// boylece "Degistirme" secilen durum alttakileri engellemez.
    /// </summary>
    public static (SonarScene Scene, string ProfileId)? Pick(AppSettings s, bool meeting, bool game, bool music)
    {
        if (meeting && s.SonarMeetingProfile.Length > 0) return (SonarScene.Meeting, s.SonarMeetingProfile);
        if (game && s.SonarGameProfile.Length > 0) return (SonarScene.Game, s.SonarGameProfile);
        if (music && !game && s.SonarMusicProfile.Length > 0) return (SonarScene.Music, s.SonarMusicProfile);
        if (!meeting && !game && !music && s.SonarIdleProfile.Length > 0) return (SonarScene.Idle, s.SonarIdleProfile);
        return null;
    }
}
