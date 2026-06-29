using System.Reflection;
using QuestPDF.Drawing;
using QuestPDF.Infrastructure;

namespace Obratka.Modules.Reports;

// Одноразовая инициализация QuestPDF: лицензия (Community — ADR-007, MIT/бесплатно для
// выручки < $1M) и регистрация Cyrillic-шрифта из embedded TTF. Системные шрифты не
// используем — на Alpine/musl (SkiaSharp NoDependencies) их нет, поэтому встроенный PT Sans
// гарантирует идентичный рендер кириллицы на Windows-dev и в контейнере.
internal static class ReportPdfBootstrap
{
    // Имя семейства как оно зашито в PT_Sans-Web-*.ttf.
    public const string FontFamily = "PT Sans";

    private static readonly object Gate = new();
    private static bool _initialized;

    public static void EnsureInitialized()
    {
        if (_initialized) return;
        lock (Gate)
        {
            if (_initialized) return;

            QuestPDF.Settings.License = LicenseType.Community;

            var assembly = typeof(ReportPdfBootstrap).Assembly;
            foreach (var name in assembly.GetManifestResourceNames())
            {
                if (!name.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase)) continue;
                using var stream = assembly.GetManifestResourceStream(name);
                if (stream is null) continue;
                FontManager.RegisterFont(stream);
            }

            _initialized = true;
        }
    }
}
