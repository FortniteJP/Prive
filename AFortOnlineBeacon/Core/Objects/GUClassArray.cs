namespace AFortOnlineBeacon.Core.Objects;

public class GUClassArray {
    public static UClass StaticClass<T>() => StaticClass(typeof(T));

    public static UClass StaticClass(Type type) {
        // TODO: Implement
        return new UClass(type);
    }
}