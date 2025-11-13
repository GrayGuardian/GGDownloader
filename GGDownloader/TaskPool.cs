using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GGDownloader
{
    public interface IRefrence
    {
        void Init();
        void OnEnable();
        void OnDispose();
    }
    public class TaskPool
    {
        private static Dictionary<Type,Stack<IRefrence>> _pool = new Dictionary<Type, Stack<IRefrence>>();
    
        public static T Get<T>() where T : IRefrence,new()
        {
            if (!_pool.ContainsKey(typeof(T))) _pool.Add(typeof(T), new Stack<IRefrence>());
            if (_pool.TryGetValue(typeof(T), out var stack) && stack.TryPop(out IRefrence data))
            {
                data.OnEnable();
                return (T)data;
            }
            data = new T();
            data.Init();
            data.OnEnable();
            return (T)data;
        }
        public static void Recycle<T>(T data) where T : IRefrence
        {
            if (!_pool.ContainsKey(typeof(T))) _pool.Add(typeof(T), new Stack<IRefrence>());
            if (_pool.TryGetValue(typeof(T), out var stack))
            {
                data.OnDispose();
                stack.Push(data);
            }
        }
    }
}
