namespace OverTranslate.Models;

/// <summary>
/// An item that can say, in one string, everything a picker's search box should be able to find it
/// by.
/// </summary>
/// <remarks>
/// The alternative was to search whatever the list happens to be showing, and that is the one thing
/// the search must not be limited to: the label a picker draws is one language's name for the item
/// (see <see cref="LangItem.Display"/>, which drops the Chinese half in an English interface), while
/// someone hunting for 日文 in an English build, or typing <c>ja</c> in a Chinese one, is searching
/// by a name that is not on screen at all. Letting the item answer means every spelling it has —
/// code, local name, English name — is reachable no matter which one is being displayed today.
/// </remarks>
public interface ISearchableItem
{
    /// <summary>Every name this item may be searched by, run together in one string.</summary>
    string SearchText { get; }
}
