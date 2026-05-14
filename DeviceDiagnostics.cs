using EightDRealtime.Audio;
using System.Text;

namespace EightDRealtime;

internal static class DeviceDiagnostics
{
    public static string ReportPath =>
        Path.Combine(AppContext.BaseDirectory, "设备诊断.log");

    public static void WriteDeviceReport(Exception? exception = null)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"系统：{Environment.OSVersion}");
        builder.AppendLine($"进程：{Environment.ProcessPath}");
        builder.AppendLine();

        try
        {
            var devices = CoreAudioDeviceEnumerator.GetActiveRenderDevices();
            builder.AppendLine($"播放设备数量：{devices.Count}");
            foreach (var device in devices)
            {
                builder.AppendLine($"- {device.Name}");
                builder.AppendLine($"  ID: {device.Id}");
                builder.AppendLine($"  默认: {(device.IsDefault ? "是" : "否")}");
            }
        }
        catch (Exception ex)
        {
            builder.AppendLine("设备枚举异常：");
            builder.AppendLine(ex.ToString());
        }

        if (exception is not null)
        {
            builder.AppendLine();
            builder.AppendLine("界面捕获到的异常：");
            builder.AppendLine(exception.ToString());
        }

        File.WriteAllText(ReportPath, builder.ToString(), Encoding.UTF8);
        try
        {
            Console.WriteLine(builder.ToString());
        }
        catch
        {
            // WinExe builds may not have an attached console.
        }
    }
}
