//using System; UniRx.ObservableExtensions conflict
using UniRx;
using UnityEngine;

public static class UniRxExtensions
{

    public static System.IObservable<T> WhereNotNull<T>(this System.IObservable<T> source)
    {
        return source.Where(x => x != null);
    }

    public static System.IDisposable SubscribeAndAddToGameObject<T>(this System.IObservable<T> source, System.Action<T> onNext, GameObject gameObject)
    {
        return source.Subscribe(onNext).AddTo(gameObject);
    }
}
