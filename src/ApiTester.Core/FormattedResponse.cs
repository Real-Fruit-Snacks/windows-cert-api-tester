namespace ApiTester.Core;

public enum BodyKind { Json, Xml, Html, Text, Binary, Empty }

public sealed record FormattedResponse(BodyKind Kind, string Text);
