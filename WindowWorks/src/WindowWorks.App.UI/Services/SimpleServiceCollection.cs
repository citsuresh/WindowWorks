using System;
using System.Collections.Concurrent;

namespace WindowWorks.App.UI.Services
{
    // Minimal service collection/provider to avoid external NuGet dependency.
    // Supports singleton registrations only (sufficient for UI services like DialogService).
    public class SimpleServiceCollection
    {
        private readonly ConcurrentDictionary<Type, Func<object>> _factories = new();

        public void AddSingleton<TService, TImplementation>()
            where TImplementation : TService, new()
        {
            _factories[typeof(TService)] = () => new TImplementation();
        }

        public IServiceProvider BuildServiceProvider()
        {
            return new SimpleServiceProvider(_factories);
        }
    }

    internal class SimpleServiceProvider : IServiceProvider
    {
        private readonly ConcurrentDictionary<Type, Func<object>> _factories;
        private readonly ConcurrentDictionary<Type, object> _singletons = new();

        public SimpleServiceProvider(ConcurrentDictionary<Type, Func<object>> factories)
        {
            _factories = factories ?? throw new ArgumentNullException(nameof(factories));
        }

        public object? GetService(Type serviceType)
        {
            if (serviceType == null) throw new ArgumentNullException(nameof(serviceType));
            if (_singletons.TryGetValue(serviceType, out var inst)) return inst;
            if (_factories.TryGetValue(serviceType, out var factory))
            {
                var created = factory();
                _singletons[serviceType] = created;
                return created;
            }
            return null;
        }
    }
}
