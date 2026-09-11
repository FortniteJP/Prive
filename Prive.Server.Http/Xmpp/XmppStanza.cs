using System.Xml;
using System.Xml.Linq;

namespace Prive.Server.Http.Xmpp;

/// <summary>
///     The XML layer. XMPP over WebSocket (RFC 7395) frames every stanza as one complete element
///     per WebSocket message, so a frame goes straight through <see cref="XElement.Parse(string)"/>
///     - none of the incremental stream parsing raw-TCP XMPP would need, and no dependency beyond
///     System.Xml.Linq.
/// </summary>
public static class XmppStanza {
    /// <summary>Replaces stream:stream on a WebSocket transport - the open/close frames.</summary>
    public static readonly XNamespace Framing = "urn:ietf:params:xml:ns:xmpp-framing";
    public static readonly XNamespace Stream = "http://etherx.jabber.org/streams";
    public static readonly XNamespace Sasl = "urn:ietf:params:xml:ns:xmpp-sasl";
    public static readonly XNamespace Bind = "urn:ietf:params:xml:ns:xmpp-bind";
    public static readonly XNamespace SessionNs = "urn:ietf:params:xml:ns:xmpp-session";
    public static readonly XNamespace Tls = "urn:ietf:params:xml:ns:xmpp-tls";
    public static readonly XNamespace RosterVer = "urn:xmpp:features:rosterver";
    public static readonly XNamespace Compress = "http://jabber.org/features/compress";
    public static readonly XNamespace IqAuth = "http://jabber.org/features/iq-auth";
    public static readonly XNamespace Client = "jabber:client";
    public static readonly XNamespace MucUser = "http://jabber.org/protocol/muc#user";

    /// <summary>
    ///     Parses one frame, or null if it is not usable XML.
    ///     <para>
    ///         A stanza arrives as a standalone fragment, so a prefix the client never declared on
    ///         it - <c>stream:</c>, <c>muc:</c> - is an XML error rather than a stanza. Those get
    ///         retried inside a wrapper that declares them, instead of dropping the frame.
    ///     </para>
    /// </summary>
    public static XElement? Parse(string frame) {
        if (string.IsNullOrWhiteSpace(frame)) return null;
        try {
            return XElement.Parse(frame);
        } catch (XmlException) { }
        try {
            // Concatenated, not interpolated: presence carries a JSON status full of braces.
            return XElement.Parse("<wrap xmlns:stream=\"http://etherx.jabber.org/streams\""
                + " xmlns:muc=\"http://jabber.org/protocol/muc\""
                + " xmlns:ping=\"urn:xmpp:ping\">" + frame + "</wrap>").Elements().FirstOrDefault();
        } catch (XmlException) {
            return null;
        }
    }

    /// <summary>One line, no indentation and no XML declaration - what goes on the wire.</summary>
    public static string Render(this XElement element) => element.ToString(SaveOptions.DisableFormatting);

    public static string? Attr(this XElement element, string name) => element.Attribute(name)?.Value;

    /// <summary>
    ///     A child by local name, ignoring whatever namespace or prefix it came in under. That is
    ///     deliberate: the client may send the MUC marker as either <c>x</c> or <c>muc:x</c>.
    /// </summary>
    public static XElement? Child(this XElement element, string localName)
        => element.Elements().FirstOrDefault(x => x.Name.LocalName == localName);

    /// <summary>The stream header. The client opens twice - once before SASL and once after.</summary>
    public static string Open(string domain, string id) => new XElement(Framing + "open",
        new XAttribute("from", domain),
        new XAttribute("id", id),
        new XAttribute("version", "1.0"),
        new XAttribute(XNamespace.Xml + "lang", "en")
    ).Render();

    /// <summary>Ends the stream. Also the only thing sent before hanging up on a bad client.</summary>
    public static string Close() => new XElement(Framing + "close").Render();

    /// <summary>
    ///     What the client may do next. Before SASL that is PLAIN; after it, bind and session -
    ///     offering mechanisms again would send the client back round the login loop.
    /// </summary>
    public static string Features(bool authenticated) => authenticated
        ? new XElement(Stream + "features",
            new XAttribute(XNamespace.Xmlns + "stream", Stream.NamespaceName),
            new XElement(RosterVer + "ver"),
            new XElement(Tls + "starttls"),
            new XElement(Bind + "bind"),
            new XElement(Compress + "compression", new XElement(Compress + "method", "zlib")),
            new XElement(SessionNs + "session")
        ).Render()
        : new XElement(Stream + "features",
            new XAttribute(XNamespace.Xmlns + "stream", Stream.NamespaceName),
            new XElement(Sasl + "mechanisms", new XElement(Sasl + "mechanism", "PLAIN")),
            new XElement(RosterVer + "ver"),
            new XElement(Tls + "starttls"),
            new XElement(Compress + "compression", new XElement(Compress + "method", "zlib")),
            new XElement(IqAuth + "auth")
        ).Render();

    public static string SaslSuccess() => new XElement(Sasl + "success").Render();

    /// <summary>The reply to _xmpp_bind1, handing the client the full JID it now owns.</summary>
    public static string BindResult(string jid) => new XElement(Client + "iq",
        new XAttribute("to", jid),
        new XAttribute("id", "_xmpp_bind1"),
        new XAttribute("type", "result"),
        new XElement(Bind + "bind", new XElement(Bind + "jid", jid))
    ).Render();

    /// <summary>An empty <c>type="result"</c>. Answers _xmpp_session1 and every keepalive ping.</summary>
    public static string IqResult(string jid, string domain, string id) => new XElement(Client + "iq",
        new XAttribute("to", jid),
        new XAttribute("from", domain),
        new XAttribute("id", id),
        new XAttribute("type", "result")
    ).Render();

    public static string Message(string from, string to, string body, string? type = null, string? id = null) {
        var message = new XElement(Client + "message",
            new XAttribute("from", from),
            new XAttribute("to", to),
            new XElement(Client + "body", body)
        );
        if (type is not null) message.SetAttributeValue("type", type);
        if (id is not null) message.SetAttributeValue("id", id);
        return message.Render();
    }

    /// <summary>
    ///     A presence update. <paramref name="status"/> is the opaque JSON blob the client puts
    ///     its party and playlist state in; <paramref name="away"/> adds the show element the
    ///     client reads as "away".
    /// </summary>
    public static string Presence(string from, string to, string status, bool away, bool offline) {
        var presence = new XElement(Client + "presence",
            new XAttribute("from", from),
            new XAttribute("to", to),
            new XAttribute("type", offline ? "unavailable" : "available")
        );
        if (away) presence.Add(new XElement(Client + "show", "away"));
        presence.Add(new XElement(Client + "status", status));
        return presence.Render();
    }

    /// <summary>
    ///     A MUC occupant notice. <paramref name="self"/> adds the status codes, of which 110 is
    ///     the one that tells the client the notice is about itself; notices about other occupants
    ///     carry none. 201 ("room created") goes out on every join, which is what the client
    ///     expects here - these rooms are created by the first join anyway.
    /// </summary>
    public static string MucPresence(string from, string to, string nick, string occupantJid, string role, bool self, bool leaving) {
        var x = new XElement(MucUser + "x",
            new XElement(MucUser + "item",
                new XAttribute("nick", nick),
                new XAttribute("jid", occupantJid),
                new XAttribute("role", role)
            )
        );
        if (!leaving) x.Element(MucUser + "item")!.SetAttributeValue("affiliation", "none");
        if (self) {
            x.Add(new XElement(MucUser + "status", new XAttribute("code", "110")));
            x.Add(new XElement(MucUser + "status", new XAttribute("code", "100")));
            x.Add(new XElement(MucUser + "status", new XAttribute("code", "170")));
            if (!leaving) x.Add(new XElement(MucUser + "status", new XAttribute("code", "201")));
        }
        var presence = new XElement(Client + "presence",
            new XAttribute("from", from),
            new XAttribute("to", to),
            x
        );
        if (leaving) presence.SetAttributeValue("type", "unavailable");
        return presence.Render();
    }
}
