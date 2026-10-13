// BuildInfo.cs — 版本信息（默认值）
//
// 这里的值是「未指定版本号时」的兜底，供 IDE 或手工 csc 编译使用。
// build.ps1 会在构建时生成 dist\_gen\BuildInfo.cs 并用它替换本文件，
// 因此正式产物的版本号来自命令行 -Version / CI 的 tag，不需要手工改这里。

using System.Reflection;

// 文件属性里显示的版本号（右键 exe → 属性 → 详细信息）
[assembly: AssemblyVersion("0.0.0.0")]
[assembly: AssemblyFileVersion("0.0.0.0")]
[assembly: AssemblyInformationalVersion("v0.0.0-dev")]

namespace OneKeyTool
{
    /// <summary>构建时注入的版本信息。</summary>
    internal static class BuildInfo
    {
        /// <summary>完整版本串，形如 v1.0.0 或 v0.0.0-dev。</summary>
        public const string Version = "v0.0.0-dev";

        /// <summary>界面显示用：产品名 + 版本号。</summary>
        public const string DisplayName = "键盘自定义键工具 " + Version;

        /// <summary>下载页面（托盘「关于」用）。程序本身不联网，仅在你点击时才打开浏览器。</summary>
        public const string ReleasesPage = "https://github.com/byhooi/OneKeyTool/releases/latest";
    }
}
