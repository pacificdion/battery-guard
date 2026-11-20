// Battery Guard - Copyright (C) 2026 Dion Fabian. Free software under the GNU General Public License v3.0; see LICENSE.
// Version and author details, compiled into every program (shown in file properties, Task
// Manager, Apps & features and the installer).
using System.Reflection;
using System.Runtime.InteropServices;

[assembly: AssemblyCompany("Dion Fabian")]
[assembly: AssemblyProduct("Battery Guard")]
[assembly: AssemblyCopyright("Copyright \u00a9 2026 Dion Fabian")]
[assembly: AssemblyTrademark("")]
[assembly: AssemblyVersion("2.1.2.0")]
[assembly: AssemblyFileVersion("2.1.2.0")]
[assembly: AssemblyInformationalVersion("2.1.2")]
[assembly: ComVisible(false)]

namespace BatteryGuard
{
    static class Product
    {
        public const string Name = "Battery Guard";
        public const string Version = "2.1.2";
        public const string Author = "Dion Fabian";
    }
}
