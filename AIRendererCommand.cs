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
