using AIRenderer.Views;
using Rhino;
using Rhino.Commands;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;
using System;
using System.Windows;

namespace AIRenderer
{
    /// <summary>
    /// New AI Render command for image-to-image generation
    /// </summary>
    public class AIRenderCommand : Command
    {
        public AIRenderCommand()
        {
            Instance = this;
        }

        public static AIRenderCommand Instance { get; private set; }

        public override string EnglishName => "AIRender";

        private Application _app;

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            try
            {
                // Ensure WPF Application exists (required on .NET Core)
                if (Application.Current == null)
                {
                    _app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                }

                IntPtr rhinoHandle = RhinoApp.MainWindowHandle();

                Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                {
                    // 只允许一个实例：再跑一次 AIRender 把已有窗口带到前台。
                    // 开两个窗口的话，各自持有一份设置快照、关窗时整份写回（后关的赢，
                    // 静默回滚先关窗口里改的东西），还会并发占用同一个侧车管道。
                    foreach (Window w in Application.Current.Windows)
                    {
                        if (w is not AIRenderWindow existing)
                            continue;

                        if (existing.WindowState == WindowState.Minimized)
                            existing.WindowState = WindowState.Normal;
                        existing.Activate();
                        RhinoApp.WriteLine("AIRender window is already open.");
                        return;
                    }

                    var window = new AIRenderWindow();
                    var helper = new System.Windows.Interop.WindowInteropHelper(window);
                    helper.Owner = rhinoHandle;
                    window.Show();
                }));

                RhinoApp.WriteLine("AIRender window opened.");
                return Result.Success;
            }
            catch (Exception ex)
            {
                RhinoApp.WriteLine($"Error opening AIRender window: {ex.Message}");
                return Result.Failure;
            }
        }
    }
}
