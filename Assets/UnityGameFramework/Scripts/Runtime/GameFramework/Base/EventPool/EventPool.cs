using System;
using System.Collections.Generic;

namespace GameFramework
{
    /// <summary>
    /// 事件池。
    /// </summary>
    /// <typeparam name="T">事件类型。</typeparam>
    internal sealed partial class EventPool<T> where T : BaseEventArgs
    {
        // 事件编号, 事件处理器链表
        private readonly GameFrameworkMultiDictionary<int, EventHandler<T>> m_EventHandlers;
        // 事件队列
        private readonly Queue<Event> m_Events;
        // 事件, 事件处理器缓存
        private readonly Dictionary<object, LinkedListNode<EventHandler<T>>> m_CachedNodes;
        // 事件, 事件处理器临时
        private readonly Dictionary<object, LinkedListNode<EventHandler<T>>> m_TempNodes;
        private readonly EventPoolMode m_EventPoolMode;
        private EventHandler<T> m_DefaultHandler;

        /// <summary>
        /// 初始化事件池的新实例。
        /// </summary>
        /// <param name="mode">事件池模式。</param>
        public EventPool(EventPoolMode mode)
        {
            m_EventHandlers = new GameFrameworkMultiDictionary<int, EventHandler<T>>();
            m_Events = new Queue<Event>();
            m_CachedNodes = new Dictionary<object, LinkedListNode<EventHandler<T>>>();
            m_TempNodes = new Dictionary<object, LinkedListNode<EventHandler<T>>>();
            m_EventPoolMode = mode;
            m_DefaultHandler = null;
        }

        /// <summary>
        /// 获取事件处理函数的数量。
        /// </summary>
        public int EventHandlerCount
        {
            get
            {
                return m_EventHandlers.Count;
            }
        }

        /// <summary>
        /// 获取事件数量。
        /// </summary>
        public int EventCount
        {
            get
            {
                return m_Events.Count;
            }
        }

        /// <summary>
        /// 事件池轮询。
        /// </summary>
        /// <param name="elapseSeconds">逻辑流逝时间，以秒为单位。</param>
        /// <param name="realElapseSeconds">真实流逝时间，以秒为单位。</param>
        public void Update(float elapseSeconds, float realElapseSeconds)
        {
            // 确保对共享资源的独占访问权限
            lock (m_Events)
            {
                while (m_Events.Count > 0)
                {
                    // 从队列中取出一个事件
                    Event eventNode = m_Events.Dequeue();
                    // 处理这个事件
                    HandleEvent(eventNode.Sender, eventNode.EventArgs);
                    // 事件归还引用池
                    ReferencePool.Release(eventNode);
                }
            }
        }

        /// <summary>
        /// 关闭并清理事件池。
        /// </summary>
        public void Shutdown()
        {
            Clear();
            m_EventHandlers.Clear();
            m_CachedNodes.Clear();
            m_TempNodes.Clear();
            m_DefaultHandler = null;
        }

        /// <summary>
        /// 清理事件。
        /// </summary>
        public void Clear()
        {
            lock (m_Events)
            {
                m_Events.Clear();
            }
        }

        /// <summary>
        /// 获取事件处理函数的数量。
        /// </summary>
        /// <param name="id">事件类型编号。</param>
        /// <returns>事件处理函数的数量。</returns>
        public int Count(int id)
        {
            GameFrameworkLinkedListRange<EventHandler<T>> range = default(GameFrameworkLinkedListRange<EventHandler<T>>);
            if (m_EventHandlers.TryGetValue(id, out range))
            {
                return range.Count;
            }

            return 0;
        }

        /// <summary>
        /// 检查是否存在事件处理函数。
        /// </summary>
        /// <param name="id">事件类型编号。</param>
        /// <param name="handler">要检查的事件处理函数。</param>
        /// <returns>是否存在事件处理函数。</returns>
        public bool Check(int id, EventHandler<T> handler)
        {
            if (handler == null)
            {
                throw new GameFrameworkException("Event handler is invalid.");
            }

            return m_EventHandlers.Contains(id, handler);
        }

        /// <summary>
        /// 订阅事件处理函数。
        /// </summary>
        /// <param name="id">事件类型编号。</param>
        /// <param name="handler">要订阅的事件处理函数。</param>
        public void Subscribe(int id, EventHandler<T> handler)
        {
            if (handler == null)
            {
                throw new GameFrameworkException("Event handler is invalid.");
            }

            if (!m_EventHandlers.Contains(id))
            {
                // 第一个事件处理器
                m_EventHandlers.Add(id, handler);
            }
            else if ((m_EventPoolMode & EventPoolMode.AllowMultiHandler) != EventPoolMode.AllowMultiHandler)
            {
                throw new GameFrameworkException(Utility.Text.Format("Event '{0}' not allow multi handler.", id));
            }
            else if ((m_EventPoolMode & EventPoolMode.AllowDuplicateHandler) != EventPoolMode.AllowDuplicateHandler && Check(id, handler))
            {
                throw new GameFrameworkException(Utility.Text.Format("Event '{0}' not allow duplicate handler.", id));
            }
            else
            {
                // 加入到事件处理器链表中(支持多个)
                m_EventHandlers.Add(id, handler);
            }
        }

        /// <summary>
        /// 取消订阅事件处理函数。
        /// </summary>
        /// <param name="id">事件类型编号。</param>
        /// <param name="handler">要取消订阅的事件处理函数。</param>
        public void Unsubscribe(int id, EventHandler<T> handler)
        {
            if (handler == null)
            {
                throw new GameFrameworkException("Event handler is invalid.");
            }

            if (m_CachedNodes.Count > 0)
            {
                // 举例: 1 -> b   事件处理器链表 a -> b -> c -> d  此时要正在运行的事件处理器为a
                // 如果此时要删除b. 那需要引入一个临时变量 1 -> c (c由b.Next获取). 最后将临时数据写回缓存表 1 -> c
                // 遍历缓存表(事件参数, 下一个事件处理器)
                foreach (KeyValuePair<object, LinkedListNode<EventHandler<T>>> cachedNode in m_CachedNodes)
                {
                    if (cachedNode.Value != null && cachedNode.Value.Value == handler)
                    {
                        // 放入临时表(事件参数, 下下个事件处理器)
                        m_TempNodes.Add(cachedNode.Key, cachedNode.Value.Next);
                    }
                }

                if (m_TempNodes.Count > 0)
                {
                    // 遍历临时表(事件参数, 下一个事件处理器)
                    foreach (KeyValuePair<object, LinkedListNode<EventHandler<T>>> cachedNode in m_TempNodes)
                    {
                        // 放回缓存表
                        m_CachedNodes[cachedNode.Key] = cachedNode.Value;
                    }

                    // 清空临时表
                    m_TempNodes.Clear();
                }
            }

            if (!m_EventHandlers.Remove(id, handler))
            {
                throw new GameFrameworkException(Utility.Text.Format("Event '{0}' not exists specified handler.", id));
            }
        }

        /// <summary>
        /// 设置默认事件处理函数。
        /// </summary>
        /// <param name="handler">要设置的默认事件处理函数。</param>
        public void SetDefaultHandler(EventHandler<T> handler)
        {
            m_DefaultHandler = handler;
        }

        /// <summary>
        /// 抛出事件，这个操作是线程安全的，即使不在主线程中抛出，也可保证在主线程中回调事件处理函数，但事件会在抛出后的下一帧分发。
        /// </summary>
        /// <param name="sender">事件源。</param>
        /// <param name="e">事件参数。</param>
        public void Fire(object sender, T e)
        {
            if (e == null)
            {
                throw new GameFrameworkException("Event is invalid.");
            }

            Event eventNode = Event.Create(sender, e);
            // 确保对共享资源的独占访问权限
            lock (m_Events)
            {
                // 将事件保存在队列中, 当前帧不处理.
                m_Events.Enqueue(eventNode);
            }
        }

        /// <summary>
        /// 抛出事件立即模式，这个操作不是线程安全的，事件会立刻分发。
        /// </summary>
        /// <param name="sender">事件源。</param>
        /// <param name="e">事件参数。</param>
        public void FireNow(object sender, T e)
        {
            if (e == null)
            {
                throw new GameFrameworkException("Event is invalid.");
            }

            // 当前帧处理. 不放到事件队列中.
            HandleEvent(sender, e);
        }

        /// <summary>
        /// 处理事件结点。
        /// </summary>
        /// <param name="sender">事件源。</param>
        /// <param name="e">事件参数。</param>
        private void HandleEvent(object sender, T e)
        {
            bool noHandlerException = false;
            GameFrameworkLinkedListRange<EventHandler<T>> range = default(GameFrameworkLinkedListRange<EventHandler<T>>);
            // 从事件处理器字典取出链表(支持多个)
            if (m_EventHandlers.TryGetValue(e.Id, out range))
            {
                LinkedListNode<EventHandler<T>> current = range.First;
                // 链表元素不为空 且 不指向尾结点
                while (current != null && current != range.Terminal)
                {
                    // 设置缓存结点字典对应值: 如果当前元素的下一个结点不是尾结点, 就设置为下一个结点; 否则设置为null
                    m_CachedNodes[e] = current.Next != range.Terminal ? current.Next : null;
                    // 执行处理
                    current.Value(sender, e);
                    // 指向下一个结点 (避免删除链表一个结点导致链表断裂问题)
                    current = m_CachedNodes[e];
                }

                // 缓存结点字典 移除 当前事件处理器
                m_CachedNodes.Remove(e);
            }
            else if (m_DefaultHandler != null)
            {
                // 默认(统一)事件处理器
                m_DefaultHandler(sender, e);
            }
            else if ((m_EventPoolMode & EventPoolMode.AllowNoHandler) == 0)
            {
                // 如果事件不允许空处理器,就抛出异常.
                noHandlerException = true;
            }

            // 事件参数归还引用池
            ReferencePool.Release(e);

            if (noHandlerException)
            {
                throw new GameFrameworkException(Utility.Text.Format("Event '{0}' not allow no handler.", e.Id));
            }
        }
    }
}
