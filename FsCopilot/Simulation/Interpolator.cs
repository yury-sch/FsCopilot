// namespace FsCopilot.Simulation;
//
// using System.Diagnostics;
//
// public interface IInterpolator<T> where T : struct
// {
//     T Lerp(in T a, in T b, double u);
// }
//
// public sealed class InterpolationQueue<T, TInterpolator>
//     where T : struct
//     where TInterpolator : IInterpolator<T>, new()
// {
//     private readonly Lock _lock = new();
//     private readonly TInterpolator _interp = new();
//
//     // ring-buffer
//     private readonly double[] _times;   // секунды (Stopwatch)
//     private readonly T[] _values;
//     private int _head;                  // индекс самого старого
//     private int _count;                 // сколько элементов в буфере сейчас
//
//     private readonly double _latencySec;
//     private readonly double _maxWindowSec;        // чуть больше latency, чтобы чистить древние
//     private static double Now() => Stopwatch.GetTimestamp() * (1.0 / Stopwatch.Frequency);
//
//     /// <param name="capacity">Размер кольцевого буфера (>=2).</param>
//     /// <param name="latency">Целевая задержка интерполяции (обычно 80–120 мс).</param>
//     public InterpolationQueue(int capacity = 128, TimeSpan? latency = null)
//     {
//         if (capacity < 2) throw new ArgumentOutOfRangeException(nameof(capacity));
//         _times = new double[capacity];
//         _values = new T[capacity];
//
//         _latencySec = (latency ?? TimeSpan.FromMilliseconds(100)).TotalSeconds;
//         _maxWindowSec = Math.Max(_latencySec * 2, 0.2); // ограничим "историю"
//     }
//
//     /// Потокобезопасно добавить сэмпл. При переполнении затирает самый старый (DropOldest).
//     public void Push(in T sample)
//     {
//         var t = Now();
//         lock (_lock)
//         {
//             Append(t, in sample);
//             TrimOld(t);
//         }
//     }
//
//     /// Потокобезопасно получить интерполированное значение на момент "сейчас - latency".
//     /// Возвращает false, если данных недостаточно.
//     public bool TryGet(out T value)
//     {
//         var target = Now() - _latencySec;
//
//         lock (_lock)
//         {
//             // прокручиваем голову, пока следующий сэмпл не позже целевого времени
//             while (_count >= 2 && TimeAt(1) <= target)
//                 Drop(1);
//
//             if (_count == 0) { value = default; return false; }
//             if (_count == 1) { value = _values[_head]; return true; }
//
//             var t0 = TimeAt(0);
//             var t1 = TimeAt(1);
//             var span = t1 - t0;
//             if (span <= 1e-12) { value = _values[Index(1)]; return true; }
//
//             var u = (target - t0) / span;
//             if (u < 0) u = 0; else if (u > 1) u = 1;
//
//             ref readonly var a = ref _values[_head];
//             ref readonly var b = ref _values[Index(1)];
//             value = _interp.Lerp(in a, in b, u);
//             return true;
//         }
//     }
//
//     // ===== внутренняя кухня =====
//
//     private void Append(double t, in T v)
//     {
//         if (_count < _values.Length)
//         {
//             _values[Index(_count)] = v;
//             _times[Index(_count)] = t;
//             _count++;
//         }
//         else
//         {
//             // переполнено — перезаписываем самый старый и двигаем голову
//             _values[_head] = v;
//             _times[_head] = t;
//             _head = (_head + 1) % _values.Length;
//         }
//     }
//
//     private void TrimOld(double now)
//     {
//         // выбрасываем слишком древние элементы по времени, чтобы окно не росло бесконечно
//         while (_count > 0)
//         {
//             if (now - _times[_head] <= _maxWindowSec) break;
//             Drop(1);
//         }
//     }
//
//     private void Drop(int k)
//     {
//         if (k <= 0) return;
//         if (k > _count) k = _count;
//         _head = (_head + k) % _values.Length;
//         _count -= k;
//     }
//
//     private int Index(int i) => (_head + i) % _values.Length;
//     private double TimeAt(int i) => _times[Index(i)];
// }