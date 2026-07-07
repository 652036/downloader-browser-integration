using LocalDownloader.Core;

namespace LocalDownloader.Tests;

public sealed class FileCategoryClassifierTests
{
    [Theory]
    [InlineData("archive.zip")]
    [InlineData("archive.rar")]
    [InlineData("archive.7z")]
    [InlineData("archive.tar")]
    [InlineData("archive.gz")]
    [InlineData("archive.tgz")]
    [InlineData("archive.bz2")]
    [InlineData("archive.xz")]
    [InlineData("archive.cab")]
    public void Classify_recognizes_archive_extensions(string fileName)
    {
        Assert.Equal(FileCategoryClassifier.Archives, FileCategoryClassifier.Classify(fileName));
    }

    [Theory]
    [InlineData("setup.exe")]
    [InlineData("setup.msi")]
    [InlineData("setup.msix")]
    [InlineData("app.apk")]
    [InlineData("app.dmg")]
    [InlineData("app.pkg")]
    [InlineData("app.deb")]
    [InlineData("app.rpm")]
    public void Classify_recognizes_program_extensions(string fileName)
    {
        Assert.Equal(FileCategoryClassifier.Programs, FileCategoryClassifier.Classify(fileName));
    }

    [Theory]
    [InlineData("movie.mp4")]
    [InlineData("movie.mkv")]
    [InlineData("movie.avi")]
    [InlineData("movie.mov")]
    [InlineData("movie.wmv")]
    [InlineData("movie.flv")]
    [InlineData("movie.webm")]
    [InlineData("movie.ts")]
    [InlineData("movie.m4v")]
    public void Classify_recognizes_video_extensions(string fileName)
    {
        Assert.Equal(FileCategoryClassifier.Video, FileCategoryClassifier.Classify(fileName));
    }

    [Theory]
    [InlineData("song.mp3")]
    [InlineData("song.flac")]
    [InlineData("song.wav")]
    [InlineData("song.aac")]
    [InlineData("song.ogg")]
    [InlineData("song.m4a")]
    [InlineData("song.wma")]
    public void Classify_recognizes_music_extensions(string fileName)
    {
        Assert.Equal(FileCategoryClassifier.Music, FileCategoryClassifier.Classify(fileName));
    }

    [Theory]
    [InlineData("doc.pdf")]
    [InlineData("doc.doc")]
    [InlineData("doc.docx")]
    [InlineData("doc.xls")]
    [InlineData("doc.xlsx")]
    [InlineData("doc.ppt")]
    [InlineData("doc.pptx")]
    [InlineData("doc.epub")]
    [InlineData("doc.mobi")]
    public void Classify_recognizes_document_extensions(string fileName)
    {
        Assert.Equal(FileCategoryClassifier.Documents, FileCategoryClassifier.Classify(fileName));
    }

    [Theory]
    [InlineData("image.iso")]
    [InlineData("noext")]
    [InlineData("weird.xyz")]
    [InlineData(null)]
    [InlineData("")]
    public void Classify_falls_back_to_other_for_unknown_extensions(string? fileName)
    {
        Assert.Equal(FileCategoryClassifier.Other, FileCategoryClassifier.Classify(fileName));
    }

    [Fact]
    public void Classify_is_case_insensitive()
    {
        Assert.Equal(FileCategoryClassifier.Video, FileCategoryClassifier.Classify("MOVIE.MP4"));
        Assert.Equal(FileCategoryClassifier.Archives, FileCategoryClassifier.Classify("Archive.ZIP"));
    }
}
