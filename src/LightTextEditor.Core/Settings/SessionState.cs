namespace LightTextEditor.Core.Settings;

/// <summary>上次关闭时打开着的文件（只记录已保存到磁盘的文件）。</summary>
public sealed class SessionState
{
    public List<SessionFile> Files { get; set; } = [];
    public int SelectedIndex { get; set; }
}

public sealed class SessionFile
{
    public string Path { get; set; } = "";
    public int CaretOffset { get; set; }
}
