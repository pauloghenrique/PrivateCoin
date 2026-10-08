using System;
using PrivateCoin.Core;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace PrivateCoin.Desktop
{
    internal static class Program
    {
        /// <summary>
        /// The main entry point for the application.
        /// </summary>
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try { FinalityPolicy.FromConfiguration(); }
            catch (Exception error)
            {
                Application.Run(new CheckpointSetupForm(error.Message));
                return;
            }
            Application.Run(new Form1());
        }
    }
}
