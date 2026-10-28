using System;
using PclExamplePlugin;

internal static class Program
{
    private static int Main()
    {
        (string Name, Action Run)[] tests =
        {
            ("已选版本保留启动按钮", SelectedInstanceKeepsLaunch),
            ("空状态恢复下载按钮", EmptyStateOffersDownload),
            ("加载期间保留禁用状态", LoadingIsUnchanged),
            ("已有下载按钮保持原状", DownloadIsUnchanged),
            ("未知按钮文案保持原状", UnknownTextIsUnchanged),
            ("重复刷新不重复修改", RepeatedRefreshIsUnchanged),
            ("空状态转为已选版本后保留启动按钮", SelectionAfterEmptyStateKeepsLaunch)
        };
        int failures = 0;
        foreach (var test in tests)
        {
            PCL.ModMinecraft.McInstanceSelected = null;
            try
            {
                test.Run();
                Console.WriteLine("通过：" + test.Name);
            }
            catch (Exception ex)
            {
                failures++;
                Console.Error.WriteLine("失败：" + test.Name + "；" + ex.Message);
            }
        }
        Console.WriteLine($"测试完成：{tests.Length - failures} 项通过，{failures} 项失败。");
        return failures == 0 ? 0 : 1;
    }

    private static void SelectedInstanceKeepsLaunch()
    {
        PCL.ModMinecraft.McInstanceSelected = new object();
        CheckRefresh("启动游戏", true, "启动游戏", true);
    }

    private static void EmptyStateOffersDownload()
    {
        CheckRefresh("启动游戏", false, "下载游戏", true);
    }

    private static void LoadingIsUnchanged()
    {
        CheckRefresh("正在加载", false, "正在加载", false);
    }

    private static void DownloadIsUnchanged()
    {
        CheckRefresh("下载游戏", true, "下载游戏", true);
    }

    private static void UnknownTextIsUnchanged()
    {
        CheckRefresh("foo", false, "foo", false);
        CheckRefresh("foo", true, "foo", true);
    }

    private static void RepeatedRefreshIsUnchanged()
    {
        var page = new LaunchPage("启动游戏", false);
        ExamplePlugin.Site9_Postfix(page);
        AssertButton(page.BtnLaunch, "下载游戏", true);
        int writes = page.BtnLaunch.Writes;
        ExamplePlugin.Site9_Postfix(page);
        AssertButton(page.BtnLaunch, "下载游戏", true);
        if (page.BtnLaunch.Writes != writes)
        {
            throw new InvalidOperationException("重复刷新再次写入了按钮状态。");
        }
    }

    private static void SelectionAfterEmptyStateKeepsLaunch()
    {
        var page = new LaunchPage("启动游戏", false);
        ExamplePlugin.Site9_Postfix(page);
        AssertButton(page.BtnLaunch, "下载游戏", true);
        PCL.ModMinecraft.McInstanceSelected = new object();
        page.BtnLaunch.Text = "启动游戏";
        page.BtnLaunch.IsEnabled = true;
        ExamplePlugin.Site9_Postfix(page);
        AssertButton(page.BtnLaunch, "启动游戏", true);
    }

    private static void CheckRefresh(string text, bool enabled, string expectedText, bool expectedEnabled)
    {
        var page = new LaunchPage(text, enabled);
        ExamplePlugin.Site9_Postfix(page);
        AssertButton(page.BtnLaunch, expectedText, expectedEnabled);
    }

    private static void AssertButton(LaunchButton button, string text, bool enabled)
    {
        if (button.Text != text || button.IsEnabled != enabled)
        {
            throw new InvalidOperationException(
                $"预期按钮为 {text}、启用状态为 {enabled}，实际为 {button.Text}、启用状态为 {button.IsEnabled}。");
        }
    }
}

internal sealed class LaunchPage
{
    public LaunchPage(string text, bool enabled)
    {
        BtnLaunch = new LaunchButton(text, enabled);
    }

    public LaunchButton BtnLaunch { get; }
}

internal sealed class LaunchButton
{
    private string _text;
    private bool _enabled;

    public LaunchButton(string text, bool enabled)
    {
        _text = text;
        _enabled = enabled;
    }

    public int Writes { get; private set; }

    public string Text
    {
        get { return _text; }
        set
        {
            _text = value;
            Writes++;
        }
    }

    public bool IsEnabled
    {
        get { return _enabled; }
        set
        {
            _enabled = value;
            Writes++;
        }
    }
}

namespace PCL
{
    /// <summary>以公开静态属性模拟官方 PCL 的版本选择入口，复现插件对成员类型的误判。</summary>
    public static class ModMinecraft
    {
        public static object? McInstanceSelected { get; set; }
    }
}
