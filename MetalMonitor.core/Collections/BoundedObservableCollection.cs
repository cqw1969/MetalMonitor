using System.Collections.ObjectModel;

namespace MetalMonitor.Core
{
    /// <summary>
    /// 有容量上限的可观察集合：元素总数超过上限时，自动移除最旧的元素（索引 0）。
    /// 用途：UI 日志面板这类"只关心最近若干条"的展示型集合——容量约束由类型本身保证，不依赖调用方手动裁剪。
    /// </summary>
    /// <remarks>
    /// 归属说明：本类型只使用 BCL 的 <see cref="ObservableCollection{T}"/>，不依赖任何 UI 框架，因此放在 Core 便于单测覆盖；
    /// 采集/业务层自身不消费它，当前唯一使用者是 UI 层的主界面日志列表。
    /// 裁剪顺序为"先裁后插"：若先插入再移除索引 0，在 index == 0 时会把刚插入的新元素当作最旧元素删掉。
    /// </remarks>
    public sealed class BoundedObservableCollection<T> : ObservableCollection<T>
    {
        /// <summary>元素总数上限；超出时从索引 0 移除最旧元素。</summary>
        private readonly int _capacity;

        /// <summary>
        /// 创建指定容量上限的集合。
        /// </summary>
        /// <param name="capacity">容量上限，必须大于 0。</param>
        /// <exception cref="ArgumentOutOfRangeException">capacity 小于等于 0 时抛出。</exception>
        public BoundedObservableCollection(int capacity)
        {
            if (capacity <= 0)
                throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "容量上限必须大于 0。");

            _capacity = capacity;
        }

        /// <summary>
        /// 插入元素：先确保剩余空间（移除最旧元素），再执行插入。
        /// </summary>
        /// <param name="index">插入位置。</param>
        /// <param name="item">待插入元素。</param>
        protected override void InsertItem(int index, T item)
        {
            int removed = 0;
            while (Count >= _capacity)
            {
                base.RemoveItem(0);
                removed++;
            }

            // 裁剪会改变元素位置：Add 传入的 index 是裁剪前的 Count，需左移相应数量后才合法
            if (removed > 0)
                index = Math.Max(0, index - removed);

            base.InsertItem(index, item);
        }
    }
}
