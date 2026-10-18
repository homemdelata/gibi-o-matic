using System.Globalization;
using System.Text;
using System.Xml;
using GibiOMatic.Core.Domain;
using GibiOMatic.Core.Text;

namespace GibiOMatic.Conversion.Cbz;

/// <summary>
/// <c>ComicInfo.xml</c> (ComicRack schema 2.0) with the fields of SPEC §9.2, read by Komga and
/// other readers.
/// </summary>
public static class ComicInfoWriter
{
    public static string Write(BookMetadata book, int? pageCount)
    {
        var settings = new XmlWriterSettings { Indent = true, Encoding = new UTF8Encoding(false), OmitXmlDeclaration = false };
        var sb = new StringBuilder();
        using (var xml = XmlWriter.Create(new StringWriterUtf8(sb), settings))
        {
            xml.WriteStartDocument();
            xml.WriteStartElement("ComicInfo");
            xml.WriteAttributeString("xmlns", "xsi", null, "http://www.w3.org/2001/XMLSchema-instance");
            xml.WriteAttributeString("xmlns", "xsd", null, "http://www.w3.org/2001/XMLSchema");

            void Element(string name, string? value)
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    xml.WriteElementString(name, value.Trim());
                }
            }

            string? Names(CreditRole role)
            {
                var names = book.Credits.Where(c => c.Role == role).Select(c => c.Name).Distinct().ToList();
                return names.Count == 0 ? null : string.Join(", ", names);
            }

            // A one-shot is a series of its own for readers: the series name is the title.
            var oneShot = book.OneShot == true || book.Series is null;
            Element("Title", book.Title);
            Element("Series", oneShot ? book.Title : book.Series!.Name);
            Element("Number", oneShot ? "1" : book.Series!.Number);
            Element("Volume", book.Series?.Volume);
            Element("SeriesGroup", book.Universes.Count == 0 ? null : string.Join(", ", book.Universes));
            if (oneShot)
            {
                Element("Count", "1");
            }

            Element("Summary", book.Summary);
            Element("Year", book.Date?.Year.ToString(CultureInfo.InvariantCulture));
            Element("Month", book.Date?.Month?.ToString(CultureInfo.InvariantCulture));
            Element("Day", book.Date?.Day?.ToString(CultureInfo.InvariantCulture));
            Element("Writer", Names(CreditRole.Writer));
            Element("Penciller", Names(CreditRole.Artist));
            Element("Inker", Names(CreditRole.Inker));
            Element("Colorist", Names(CreditRole.Colorist));
            Element("Letterer", Names(CreditRole.Letterer));
            Element("CoverArtist", Names(CreditRole.Cover));
            Element("Editor", Names(CreditRole.Editor));
            Element("Translator", Names(CreditRole.Translator));
            Element("Publisher", book.Publisher);
            Element("Imprint", book.Imprint);
            Element("Tags", book.Tags.Count == 0 ? null : string.Join(", ", book.Tags));
            Element("Web", ComicVineUrl(book.Identifiers.ComicVine));
            Element("PageCount", pageCount?.ToString(CultureInfo.InvariantCulture));
            Element("LanguageISO", book.Language);
            Element("Format", book.Kind switch
            {
                BookKind.Issue => "Issue",
                BookKind.Omnibus => "Omnibus",
                BookKind.Artbook => "Artbook",
                BookKind.Tpb => "TPB",
                _ => null,
            });
            Element("GTIN", Isbn.IsValid(book.Identifiers.Isbn) ? Isbn.Normalize(book.Identifiers.Isbn!) : null);
            xml.WriteEndElement();
            xml.WriteEndDocument();
        }

        return sb.ToString();
    }

    /// <summary>ComicVine resolves "/x/4000-937068/" to the issue page whatever the slug.</summary>
    private static string? ComicVineUrl(string? id) =>
        id is { Length: > 0 } ? $"https://comicvine.gamespot.com/issue/{id}/" : null;

    private sealed class StringWriterUtf8(StringBuilder sb) : StringWriter(sb, CultureInfo.InvariantCulture)
    {
        public override Encoding Encoding => new UTF8Encoding(false);
    }
}
