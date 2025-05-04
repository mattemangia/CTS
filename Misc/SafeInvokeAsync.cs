using System;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CTS
{
    public static class ControlExtensions
    {
        public static async Task SafeInvokeAsync(this Control control, Action action)
        {
            if (control.InvokeRequired)
            {
                await Task.Run(() => control.Invoke(action));
            }
            else
            {
                action();
            }
        }
    }
}