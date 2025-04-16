using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CTSegmenter
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
