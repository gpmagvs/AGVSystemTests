using System.Collections.Concurrent;
using System.Reflection;
using AGVSystemCommonNet6.MAP;
using VMSystem.AGV;

namespace AGVSystemTests.VMSystemTests
{
    /// <summary>
    /// 以 <see cref="DispatchProxy"/> 建立的 IAGV 假物件 (不引入額外的 mock 套件)。
    /// - 事件 (add_/remove_) 以真正的 Delegate 儲存，可查詢訂閱數量、可手動觸發。
    /// - 其餘屬性回傳 default；Task / Task&lt;T&gt; 回傳已完成的 Task；currentMapPoint 回傳固定點位。
    /// </summary>
    public class FakeAgvProxy : DispatchProxy
    {
        private readonly ConcurrentDictionary<string, Delegate?> _events = new();
        public string AgvName { get; set; } = "FakeAGV";
        public MapPoint CurrentPoint { get; set; } = new MapPoint { TagNumber = 1 };

        public int SubscriberCount(string eventName)
            => _events.TryGetValue(eventName, out var d) && d != null ? d.GetInvocationList().Length : 0;

        public Delegate? GetEvent(string eventName)
            => _events.TryGetValue(eventName, out var d) ? d : null;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod == null) return null;
            var name = targetMethod.Name;
            if (name.StartsWith("add_"))
            {
                _events.AddOrUpdate(name[4..], _ => (Delegate)args![0]!, (_, cur) => Delegate.Combine(cur, (Delegate)args![0]!));
                return null;
            }
            if (name.StartsWith("remove_"))
            {
                _events.AddOrUpdate(name[7..], _ => null, (_, cur) => Delegate.Remove(cur, (Delegate)args![0]!));
                return null;
            }
            switch (name)
            {
                case "get_Name": return AgvName;
                case "get_AgvIDStr": return AgvName;
                case "get_currentMapPoint": return CurrentPoint;
            }
            var rt = targetMethod.ReturnType;
            if (rt == typeof(void)) return null;
            if (rt == typeof(Task)) return Task.CompletedTask;
            if (rt.IsGenericType && rt.GetGenericTypeDefinition() == typeof(Task<>))
            {
                var inner = rt.GetGenericArguments()[0];
                object? def = inner.IsValueType ? Activator.CreateInstance(inner) : null;
                return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(inner).Invoke(null, new[] { def });
            }
            return rt.IsValueType ? Activator.CreateInstance(rt) : null;
        }
    }

    public static class FakeAgvFactory
    {
        public static IAGV Create(out FakeAgvProxy proxy, string name = "FakeAGV")
        {
            var agv = DispatchProxy.Create<IAGV, FakeAgvProxy>();
            proxy = (FakeAgvProxy)(object)agv;
            proxy.AgvName = name;
            return agv;
        }
    }
}
