using System;
using System.Collections.Generic;
using System.Linq;

namespace TerrarAI.Content.Companion;

public interface IContainerItems<T>
{
    T Clone(T item);
    int Type(T item);
    int Count(T item);
    int Limit(T item);
    bool Favorite(T item);
    bool CanStack(T destination, T source);
    T Split(T source, int count);
    int Stack(T destination, T source, int count);
    void Consume(T item, int count);
}
public sealed record ContainerOrder(int Type, int Count);
public sealed record ContainerReceipt(int Box, int Type, int Count);

// All operations mutate private clones. A caller commits only a fully successful
// transaction after revalidating its native inventory/container snapshot.
public sealed class ContainerInventory<T>
{
    public T[] Bag { get; }
    public T[][] Boxes { get; }
    public List<ContainerReceipt> Receipts { get; } = new();
    public bool ReadyToCommit { get; private set; }
    private bool _started;
    private readonly IContainerItems<T> _items;
    public ContainerInventory(IReadOnlyList<T> bag, IEnumerable<IReadOnlyList<T>> boxes, IContainerItems<T> items)
    { _items = items; Bag = bag.Select(items.Clone).ToArray(); Boxes = boxes.Select(b => b.Select(items.Clone).ToArray()).ToArray(); }

    public bool Transfer(bool deposit, IReadOnlyList<ContainerOrder> orders, out string error)
    {
        error = ""; ReadyToCommit = false;
        if (_started) { error = "存取事务已使用，请重新读取现场。"; return false; }
        _started = true;
        if (orders.Count == 0 || orders.Any(o => o.Type < 1 || o.Count < 0 || !deposit && o.Count == 0) || orders.Select(o => o.Type).Distinct().Count() != orders.Count)
        { error = "存取项目或数量无效。"; return false; }
        foreach (var order in orders)
        {
            var sources = deposit ? Bag.Select((item, slot) => (item, box: -1, slot)).ToArray() :
                Boxes.SelectMany((b, box) => b.Select((item, slot) => (item, box, slot))).ToArray();
            var usable = sources.Where(s => _items.Type(s.item) == order.Type && _items.Count(s.item) > 0 && (!deposit || !_items.Favorite(s.item))).ToArray();
            int available = usable.Sum(s => _items.Count(s.item)), amount = order.Count == 0 ? available : order.Count;
            if (amount < 1 || available < amount) { error = $"可存取物品不足（需要{amount}，可用{available}；收藏物品不自动存入）。"; return false; }
            int left = amount;
            foreach (var source in usable)
            {
                var targets = deposit ? Boxes.SelectMany((b, box) => b.Select((item, slot) => (item, box, slot))).ToArray() :
                    Bag.Select((item, slot) => (item, box: -1, slot)).ToArray();
                foreach (var target in targets.OrderBy(t => _items.Count(t.item) > 0 ? 0 : 1))
                {
                    int count = Math.Min(left, _items.Count(source.item)); if (count <= 0) break;
                    if (_items.Count(target.item) > 0)
                    {
                        if (_items.Type(target.item) != order.Type || !_items.CanStack(target.item, source.item)) continue;
                        count = Math.Min(count, Math.Max(0, _items.Limit(target.item) - _items.Count(target.item)));
                        if (count == 0) continue;
                        int beforeSource = _items.Count(source.item), beforeTarget = _items.Count(target.item);
                        if (_items.Stack(target.item, source.item, count) != count || _items.Count(source.item) != beforeSource - count ||
                            _items.Count(target.item) != beforeTarget + count || _items.Type(target.item) != order.Type)
                            throw new InvalidOperationException("物品堆叠规则改变了搬运数量。");
                    }
                    else
                    {
                        count = Math.Min(count, _items.Limit(source.item)); if (count <= 0) continue;
                        int before = _items.Count(source.item); var moved = _items.Split(source.item, count);
                        if (_items.Count(moved) != count || _items.Type(moved) != order.Type || _items.Count(source.item) != before - count)
                            throw new InvalidOperationException("物品拆分规则改变了搬运数量。");
                        if (deposit) Boxes[target.box][target.slot] = moved; else Bag[target.slot] = moved;
                    }
                    Receipts.Add(new(deposit ? target.box : source.box, order.Type, count)); left -= count;
                }
                if (left == 0) break;
            }
            if (left > 0) { error = deposit ? "附近箱子空间不足，未搬运物品。" : "主背包空间不足，未搬运物品。"; return false; }
        }
        ReadyToCommit = true; return true;
    }

    public bool Insert(T output)
    {
        ReadyToCommit = false;
        foreach (int slot in Enumerable.Range(0, Bag.Length).OrderBy(i => _items.Count(Bag[i]) > 0 ? 0 : 1))
        {
            int left = _items.Count(output); if (left == 0) return true;
            var target = Bag[slot];
            if (_items.Count(target) == 0)
            {
                int n = Math.Min(left, _items.Limit(output)), type = _items.Type(output);
                var moved = _items.Split(output, n);
                if (_items.Count(moved) != n || _items.Type(moved) != type || _items.Count(output) != left - n)
                    throw new InvalidOperationException("合成产物拆分数量已变化。");
                Bag[slot] = moved;
            }
            else if (_items.Type(target) == _items.Type(output) && _items.CanStack(target, output))
            {
                int n = Math.Min(left, Math.Max(0, _items.Limit(target) - _items.Count(target)));
                int before = _items.Count(target), type = _items.Type(output);
                if (n > 0 && (_items.Stack(target, output, n) != n || _items.Count(target) != before + n || _items.Count(output) != left - n || _items.Type(target) != type))
                    throw new InvalidOperationException("合成产物堆叠数量已变化。");
            }
        }
        return _items.Count(output) == 0;
    }

    public bool Craft(IReadOnlyList<IngredientRequirement> requirements, T output, out string error, out bool materialsReady)
    {
        error = "合成事务已使用，请重新读取现场。"; materialsReady = false; ReadyToCommit = false;
        if (_started) return false; _started = true;
        var stock = Bag.Concat(Boxes.SelectMany(b => b)).Where(i => _items.Count(i) > 0)
            .GroupBy(_items.Type).ToDictionary(g => g.Key, g => g.Sum(_items.Count));
        var allocation = IngredientAllocation.Plan(stock, requirements);
        error = "材料不足。"; materialsReady = allocation.Missing.Count == 0;
        if (!materialsReady) return false;
        foreach (var (type, amount) in allocation.Used)
        {
            int left = amount;
            void Consume(T item, int box)
            {
                int before = _items.Count(item), n = Math.Min(before, left);
                if (n < 1 || _items.Type(item) != type) return;
                _items.Consume(item, n);
                if (_items.Count(item) != before - n) throw new InvalidOperationException("合成扣料数量已变化。");
                left -= n; if (box >= 0) Receipts.Add(new(box, type, n));
            }
            foreach (var item in Bag) { Consume(item, -1); if (left == 0) break; }
            for (int b = 0; b < Boxes.Length && left > 0; b++) foreach (var item in Boxes[b]) { Consume(item, b); if (left == 0) break; }
            if (left > 0) throw new InvalidOperationException("合成材料快照已变化。");
        }
        if (!Insert(output)) { error = "主背包放不下合成产物，未扣背包或箱子材料。"; return false; }
        ReadyToCommit = true; error = ""; return true;
    }
}
