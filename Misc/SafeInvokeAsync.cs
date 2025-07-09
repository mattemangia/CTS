//Copyright 2025 Matteo Mangiagalli - matteo.mangiagalli@unifr.ch
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
        public static void SetStyle(this Control control, ControlStyles style, bool value)
        {
            Type type = control.GetType();
            System.Reflection.MethodInfo method = type.GetMethod("SetStyle",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

            if (method != null)
            {
                method.Invoke(control, new object[] { style, value });
            }
        }
    }
}