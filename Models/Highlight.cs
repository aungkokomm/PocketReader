namespace PocketReader.Models;

public class Highlight
{
    public int Id { get; set; }
    public int ArticleId { get; set; }
    public int StartOffset { get; set; }
    public int EndOffset { get; set; }
    public string Text { get; set; }
    public string Color { get; set; }
    public string CreatedAt { get; set; }
}
