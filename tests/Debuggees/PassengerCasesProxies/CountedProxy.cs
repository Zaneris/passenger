namespace PassengerCasesProxies;

/// <summary>What a type of another assembly gives its proxy.</summary>
public interface ICounted
{
	int Count { get; }
}

/// <summary>The proxy a type of PassengerCases names by its assembly-qualified name.</summary>
public sealed class CountedProxy
{
	private readonly ICounted _counted;

	public CountedProxy(ICounted counted) => _counted = counted;

	public int Doubled => _counted.Count * 2;
}
