using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace sutty.UI.Helpers;

/// <summary>
/// Enumerates the font families registered with Windows. GDI is used here instead of
/// shipping another UI dependency; the returned names are also understood by xterm.js
/// and WinUI's FontFamily.
/// </summary>
public static class InstalledFontCatalog
{
    private const byte DefaultCharset = 1;
    private static readonly Lazy<Task<IReadOnlyList<string>>> CachedFonts = new(
        () => Task.Run<IReadOnlyList<string>>(Enumerate),
        LazyThreadSafetyMode.ExecutionAndPublication);

    public static Task<IReadOnlyList<string>> GetAsync() => CachedFonts.Value;

    private static IReadOnlyList<string> Enumerate()
    {
        var families = new SortedSet<string>(StringComparer.CurrentCultureIgnoreCase);
        var deviceContext = GetDC(IntPtr.Zero);
        if (deviceContext == IntPtr.Zero)
            return FallbackFonts;

        try
        {
            var filter = new LogFont
            {
                CharacterSet = DefaultCharset,
                FaceName = string.Empty,
            };
            EnumFontFamilyCallback callback = (fontData, _, _, _) =>
            {
                var font = Marshal.PtrToStructure<LogFont>(fontData);
                var name = font.FaceName?.Trim();
                if (!string.IsNullOrWhiteSpace(name) && !name.StartsWith('@'))
                    families.Add(name);
                return 1;
            };

            EnumFontFamiliesEx(deviceContext, ref filter, callback, IntPtr.Zero, 0);
            GC.KeepAlive(callback);
        }
        finally
        {
            _ = ReleaseDC(IntPtr.Zero, deviceContext);
        }

        return families.Count == 0 ? FallbackFonts : [.. families];
    }

    private static readonly IReadOnlyList<string> FallbackFonts =
        ["Cascadia Mono", "Consolas"];

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct LogFont
    {
        public int Height;
        public int Width;
        public int Escapement;
        public int Orientation;
        public int Weight;
        public byte Italic;
        public byte Underline;
        public byte StrikeOut;
        public byte CharacterSet;
        public byte OutPrecision;
        public byte ClipPrecision;
        public byte Quality;
        public byte PitchAndFamily;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string FaceName;
    }

    private delegate int EnumFontFamilyCallback(
        IntPtr fontData,
        IntPtr textMetric,
        uint fontType,
        IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr windowHandle);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr windowHandle, IntPtr deviceContext);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, EntryPoint = "EnumFontFamiliesExW")]
    private static extern int EnumFontFamiliesEx(
        IntPtr deviceContext,
        ref LogFont logFont,
        EnumFontFamilyCallback callback,
        IntPtr parameter,
        uint flags);
}
