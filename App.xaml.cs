using System.Text;

namespace FolderCompareWPF
{
    public partial class App : System.Windows.Application
    {
        public App()
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }

        protected override void OnStartup(System.Windows.StartupEventArgs e)
        {
            base.OnStartup(e);
            // i-luncher AppKit: 설치된 사본이면 시작할 때 GitHub 릴리스를 확인해 스스로 업데이트 (AppKit\AppUpdater.cs)
            ILuncher.AppKit.AppUpdater.StartBackground(ready => Dispatcher.BeginInvoke(() =>
            {
                if (System.Windows.MessageBox.Show($"새 버전 {ready.Update.Version}을(를) 받았습니다. (지금 {ready.CurrentVersion})\n\n지금 다시 시작할까요? [아니요]를 누르면 종료할 때 적용됩니다.",
                        "Folder Compare 업데이트", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Information) == System.Windows.MessageBoxResult.Yes)
                {
                    ready.RestartNow();
                    Shutdown();
                }
            }));
        }
    }
}
