namespace Shifts_Tools.Services.Plumbing.Authorization
{
    /// <summary>
    /// Базовый интерфейс для определения разрешения
    /// </summary>
    public interface IPermission
    {
        string Resource { get; }
        string? Scope { get; }
        string DisplayName { get; }
        string? Description { get; }
    }

    /// <summary>
    /// Базовая реализация разрешения
    /// </summary>
    public record Permission(string Resource, string? Scope = null) : IPermission
    {
        public string DisplayName => string.IsNullOrEmpty(Scope)
            ? Resource
            : $"{Resource}:{Scope}";

        public string? Description { get; init; }

        public override string ToString() => DisplayName;
    }
}
