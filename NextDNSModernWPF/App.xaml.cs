using System;
using System.Windows;

namespace NextDNSModernWPF
{
    // Aquí estaba el error. Cambiamos ": Application" por ": System.Windows.Application"
    public partial class App : System.Windows.Application
    {
    }
}