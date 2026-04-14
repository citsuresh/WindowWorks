using System;

namespace WindowWorks.App.UI
{
    // Lightweight static holder for the IServiceProvider until full DI lifetime is setup in WPF startup.
    public static class AppServices
    {
        public static IServiceProvider? Provider { get; set; }

        public static T? GetService<T>() where T : class
        {
            return Provider?.GetService(typeof(T)) as T;
        }
    }
}
