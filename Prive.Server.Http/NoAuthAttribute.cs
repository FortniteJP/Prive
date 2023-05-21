namespace Prive.Server.Http;

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public class NoAuthAttribute : Attribute {}