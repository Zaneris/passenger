using System.ComponentModel;
using Passenger.Text;

namespace Passenger.App.Workspace;

/// <summary>The one document a view shows, followed as it changes through one subscription: another shown lets go of the
/// last one's, so a view never hears a document it no longer shows.</summary>
public sealed class FollowedDocument
{
    private readonly Action<OpenDocument> follow;
    private readonly Action<OpenDocument> unfollow;

    private FollowedDocument(Action<OpenDocument> follow, Action<OpenDocument> unfollow)
    {
        this.follow = follow;
        this.unfollow = unfollow;
    }

    /// <summary>The document followed, null for none.</summary>
    public OpenDocument? Document { get; private set; }

    /// <summary>Follows a document's properties (its marks, pins and annotations), each change given to <paramref name="changed"/>.</summary>
    public static FollowedDocument OfProperties(PropertyChangedEventHandler changed) =>
        new(document => document.PropertyChanged += changed, document => document.PropertyChanged -= changed);

    /// <summary>Follows a document's text, each edit given to <paramref name="changed"/>.</summary>
    public static FollowedDocument OfText(EventHandler<DocumentChangedEventArgs> changed) =>
        new(document => document.Text.Changed += changed, document => document.Text.Changed -= changed);

    /// <summary>Follows <paramref name="next"/> in place of the document followed: whether it is another.</summary>
    public bool Show(OpenDocument? next)
    {
        if (ReferenceEquals(next, Document))
        {
            return false;
        }
        if (Document is not null)
        {
            unfollow(Document);
        }
        Document = next;
        if (next is not null)
        {
            follow(next);
        }
        return true;
    }
}
