using AsterismApp;

if (args is ["--render-inline-sample", var inlineOutputPath])
{
    var sample = "## 제목 수정\n\n일반 문장과 **굵은 텍스트** 그리고 *기울임*.\n\n- 목록 항목\n\n[링크](https://example.com)\n\n```python\n# comment\nprint('code')\n```\n\n수식 $x + y$";
    var html = MarkdownPreviewRenderer.Render(sample, Path.GetDirectoryName(inlineOutputPath)!, editSession: new PreviewEditSession(sample));
    html = html.Replace("<head>", "<head><script>window.chrome=window.chrome||{};window.chrome.webview={postMessage:m=>{document.documentElement.dataset.lastMessage=JSON.stringify(m);if(m.type==='inline-edit')document.documentElement.dataset.lastEdit=JSON.stringify(m);}};</script>");
    File.WriteAllText(inlineOutputPath, html);
    return;
}

if (args is ["--render-sample", var outputPath])
{
    var sample = """
# Confusion Matrix(혼동행렬)

이진 분류 모델의 **예측값**과 **실제값**을 비교하는 표.

|  | 실제 Positive | 실제 Negative |
| --- | --- | --- |
| **예측 Positive** | TP | FP |
| **예측 Negative** | FN | TN |

- **TP (True Positive)**: 실제 양성 → 양성 예측
- **TN (True Negative)**: 실제 음성 → 음성 예측
- **FP (False Positive)**: 실제 음성 → 양성으로 잘못 예측
- **FN (False Negative)**: 실제 양성 → 음성으로 잘못 예측

---

## 주요 평가 지표

| 지표 | 공식 | 의미 |
| --- | --- | --- |
| **Accuracy 정확도** | `(TP + TN) / 전체` | 전체 중 맞춘 비율 |
| **Precision 정밀도** | `TP / (TP + FP)` | 양성 예측 중 진짜 양성 |
| **Recall 재현율** | `TP / (TP + FN)` | 실제 양성 중 찾아낸 비율 |
| **Specificity 특이도** | `TN / (TN + FP)` | 실제 음성 중 찾아낸 비율 |

### 핵심 암기

```text
Precision = TP / (TP + FP)
Recall = TP / (TP + FN)
Specificity = TN / (TN + FP)
Accuracy = (TP + TN) / (TP + TN + FP + FN)
```

특히 **Precision**과 **Recall**의 차이만 확실히 기억하면 됩니다.
""";
    File.WriteAllText(outputPath, MarkdownPreviewRenderer.Render(sample, Path.GetDirectoryName(outputPath)!), System.Text.Encoding.UTF8);
    return;
}

var root = Path.Combine(Path.GetTempPath(), "graph-notes-check-" + Guid.NewGuid());
Directory.CreateDirectory(root);
try
{
    NotebookChecks.Run(root);
    if (args is ["--notebooks"]) return;
    var uiSettingsPath = Path.Combine(root, "ui-layout.json");
    var uiSettingsService = new UiLayoutSettingsService(uiSettingsPath);
    if (uiSettingsService.Load() != UiLayoutSettings.Default) throw new Exception("UI 배치 기본값 로드 실패");
    uiSettingsService.Save(new UiLayoutSettings(.72, true, 420, 1.15, "blue", "light", 310));
    var savedUiSettings = uiSettingsService.Load();
    if (Math.Abs(savedUiSettings.PreviewRatio - .72) > .001
        || !savedUiSettings.ExplorerCollapsed
        || Math.Abs(savedUiSettings.InspectorWidth - 420) > .001
        || Math.Abs(savedUiSettings.ExplorerWidth - 310) > .001
        || Math.Abs(savedUiSettings.FontScale - 1.15) > .001
        || savedUiSettings.AccentTheme != "blue"
        || savedUiSettings.SurfaceTheme != "light")
        throw new Exception("UI 배치와 모양 설정 저장 실패");

    var chunkNote = new NoteInfo(
        "데이터베이스 성능",
        Path.Combine(root, "chunk.md"),
        "## 커넥션 풀\n\nDB 연결을 재사용한다.\n\n## 인덱스\n\n검색 속도를 높인다.",
        DateTime.Now,
        NoteMetadata.Manual);
    var semanticChunks = SemanticTextChunker.Split(chunkNote);
    if (semanticChunks.Count == 0
        || semanticChunks.Select(chunk => chunk.Key).Distinct().Count() != semanticChunks.Count
        || semanticChunks.Any(chunk => string.IsNullOrWhiteSpace(chunk.ContentHash)))
        throw new Exception("제목 수준 로컬 AI 청크 생성 실패");

    var modelAssets = Path.Combine(Environment.CurrentDirectory, "Assets", "SemanticModel");
    using (var embeddingModel = new LocalEmbeddingModel(modelAssets, Path.Combine(root, "models")))
    {
        var pool = embeddingModel.Embed("데이터베이스 연결을 미리 만들어 재사용하는 커넥션 풀");
        var similarPool = embeddingModel.Embed("DB 접속 연결을 여러 개 준비해 반복해서 사용하는 방식");
        var unrelated = embeddingModel.Embed("수채화 물감으로 풍경화를 그리는 방법");
        static double Dot(float[] left, float[] right) => left.Zip(right, (a, b) => a * b).Sum();
        var similarScore = Dot(pool, similarPool);
        var unrelatedScore = Dot(pool, unrelated);
        if (pool.Length != 384 || similarScore <= unrelatedScore)
            throw new Exception($"오프라인 다국어 임베딩 추론 실패: 유사 {similarScore:F4}, 무관 {unrelatedScore:F4}");
    }

    using (var semanticModel = new LocalEmbeddingModel(modelAssets, Path.Combine(root, "models")))
    using (var semanticService = new SemanticLinkService(
        semanticModel,
        new SemanticIndexStore(Path.Combine(root, "semantic-index.db"))))
    {
        var semanticNotes = new[]
        {
            new NoteInfo("커넥션 풀", Path.Combine(root, "pool.md"), "데이터베이스 연결을 미리 여러 개 만들고 반복해서 재사용한다.", DateTime.Now, NoteMetadata.Manual),
            new NoteInfo("DB 연결 재사용", Path.Combine(root, "reuse.txt"), "DB 접속 연결을 준비해 두고 요청마다 빌려 쓰는 방식이다.", DateTime.Now, new NoteMetadata("Text", DateTime.Today, "File", "Text"), IsPlainText: true),
            new NoteInfo("수채화", Path.Combine(root, "paint.md"), "물감과 붓으로 풍경화를 그리는 방법을 기록한다.", DateTime.Now, NoteMetadata.Manual)
        };
        var firstIndex = await semanticService.BuildAsync(root, semanticNotes);
        if (!firstIndex.SuggestionsByPath[semanticNotes[0].Path].Any(item => item.Note.Path == semanticNotes[1].Path)
            || firstIndex.SuggestionsByPath[semanticNotes[0].Path].Any(item => item.Note.Path == semanticNotes[2].Path))
            throw new Exception("의미 기반 노트 연결 추천 실패");
        var secondIndex = await semanticService.BuildAsync(root, semanticNotes);
        if (secondIndex.EmbeddedChunkCount != 0 || secondIndex.ReusedChunkCount == 0)
            throw new Exception("변경되지 않은 임베딩 캐시 재사용 실패");
    }

    var store = new NoteRepository(root);
    var linkService = new NoteLinkService();
    var source = store.Create("원본");
    var target = store.Create("연결됨");
    source = store.Save(source.Path, source.Title, "[[연결됨]]", NoteMetadata.Manual);
    var study = new NoteMetadata("Database", new DateTime(2026, 8, 31), "ChatGPT", "Study");
    var imported = store.Create("Connection Pool", study);
    var fallbackTrash = Path.Combine(root, ".trash");
    Directory.CreateDirectory(fallbackTrash);
    File.WriteAllText(Path.Combine(fallbackTrash, "삭제된 노트.md"), "# 삭제된 노트");
    var notes = store.Load();
    if (notes.Any(note => note.Title == "삭제된 노트")) throw new Exception("휴지통 노트 제외 실패");
    var links = linkService.Build(notes);
    if (!links[source.Title].SequenceEqual([target.Title])) throw new Exception("내부 링크 인식 실패");
    var reloaded = notes.Single(note => note.Path == imported.Path).Metadata;
    if (reloaded.Category != study.Category || reloaded.Source != study.Source) throw new Exception("YAML 속성 저장 실패");
    var folder = Path.Combine(root, "분류");
    var movedTarget = store.Move(target.Path, folder);
    if (!File.Exists(movedTarget.Path) || !Path.GetDirectoryName(movedTarget.Path)!.Equals(folder, StringComparison.OrdinalIgnoreCase) || store.Load().All(note => note.Path != movedTarget.Path)) throw new Exception("노트 폴더 이동 실패");
    var renamedTarget = store.Rename(movedTarget.Path, "이름이 바뀐 노트");
    if (File.Exists(movedTarget.Path) || !File.Exists(renamedTarget.Path) || renamedTarget.Title != "이름이 바뀐 노트" || Path.GetFileNameWithoutExtension(renamedTarget.Path) != "이름이 바뀐 노트") throw new Exception("노트와 실제 파일 이름 변경 실패");
    var createdFolder = store.CreateFolder(folder, "새 폴더");
    if (!Directory.Exists(createdFolder) || Path.GetDirectoryName(createdFolder) != folder) throw new Exception("실제 폴더 생성 실패");
    var noteCreatedFromFolderMenu = store.CreateInFolder(createdFolder, "폴더 안 새 노트");
    if (!File.Exists(noteCreatedFromFolderMenu.Path) || Path.GetDirectoryName(noteCreatedFromFolderMenu.Path) != createdFolder) throw new Exception("선택한 폴더 내부 새 노트 생성 실패");
    var dropTarget = store.CreateFolder(root, "드롭 대상");
    var draggedNote = store.Move(renamedTarget.Path, dropTarget);
    if (!File.Exists(draggedNote.Path) || Path.GetDirectoryName(draggedNote.Path) != dropTarget) throw new Exception("드래그 노트 이동 기반 작업 실패");
    var movedFolder = store.MoveFolder(folder, dropTarget);
    if (!Directory.Exists(movedFolder) || Directory.Exists(folder) || !Directory.Exists(Path.Combine(movedFolder, "새 폴더"))) throw new Exception("드래그 폴더 이동 기반 작업 실패");
    var blockedDescendantMove = false;
    try { store.MoveFolder(movedFolder, Path.Combine(movedFolder, "새 폴더")); }
    catch (InvalidOperationException) { blockedDescendantMove = true; }
    if (!blockedDescendantMove) throw new Exception("폴더를 자기 하위로 이동하는 작업 차단 실패");
    var orderStorageDirectory = Path.Combine(root, ".check-order-state");
    var treeService = new VaultTreeService(orderStorageDirectory);
    var cachedFolders = treeService.LoadFolders(root);
    var treeItems = treeService.Build(root, store.Load(), cachedFolders, new HashSet<string>([dropTarget], StringComparer.OrdinalIgnoreCase));
    if (!treeItems.Any(item => item.IsFolder && item.Path == dropTarget) || !treeItems.Any(item => item.Note?.Path == draggedNote.Path)) throw new Exception("저장소 폴더 트리 구성 실패");
    if (treeItems.Any(item => item.IsRoot || item.Path.Equals(root, StringComparison.OrdinalIgnoreCase))
        || treeItems.Where(item => item.Depth == 0).Any(item => item.IsRoot))
        throw new Exception("탐색기 단일 저장소 루트 숨김 실패");
    if (treeItems.Any(item => item.IsFolder && item.FolderIconOpacity != 1) || treeItems.Any(item => !item.IsFolder && item.FolderIconOpacity != 0)) throw new Exception("폴더 아이콘 표시 구분 실패");
    var collapsedFolderMarker = new VaultItem("folder", Path.Combine(root, "folder"), true, false, false, 2, null);
    var expandedFolderMarker = collapsedFolderMarker with { IsExpanded = true };
    var noteMarker = new VaultItem("note", Path.Combine(root, "note.md"), false, false, false, 2, null);
    if (collapsedFolderMarker.Indent != 28
        || collapsedFolderMarker.CollapsedChevronOpacity != 1
        || expandedFolderMarker.ExpandedChevronOpacity != 1
        || noteMarker.NoteDotOpacity != 1
        || noteMarker.CollapsedChevronOpacity != 0
        || noteMarker.ExpandedChevronOpacity != 0)
        throw new Exception("탐색기 계층 표시 벡터 정렬 상태 실패");
    var nestedNote = store.CreateInFolder(Path.Combine(movedFolder, "새 폴더"), "깊은 노트");
    var ancestors = treeService.AncestorFolders(root, nestedNote.Path);
    if (!ancestors.SequenceEqual([Path.GetDirectoryName(nestedNote.Path)!, movedFolder, dropTarget, root], StringComparer.OrdinalIgnoreCase)) throw new Exception("선택 노트의 상위 폴더 경로 계산 실패");
    var sortRoot = Path.Combine(root, "정렬 검사");
    Directory.CreateDirectory(sortRoot);
    var sortStore = new NoteRepository(sortRoot);
    sortStore.CreateFolder(sortRoot, "10 폴더");
    sortStore.CreateFolder(sortRoot, "2 폴더");
    sortStore.Create("10 노트");
    sortStore.Create("2 노트");
    var sortedItems = treeService.Build(sortRoot, sortStore.Load(), treeService.LoadFolders(sortRoot), new HashSet<string>(StringComparer.OrdinalIgnoreCase));
    if (!sortedItems.Select(item => item.Name).SequenceEqual(["2 폴더", "10 폴더", "2 노트", "10 노트"])) throw new Exception("폴더 우선 이름 자연 정렬 실패");
    if (!sortedItems.Select(item => item.OrderNumber).SequenceEqual(["1", "2", "3", "4"])
        || sortedItems[0].DisplayName != "1  2 폴더")
        throw new Exception("탐색기 계층 순서 번호 표시 실패");
    var sortFolders = treeService.LoadFolders(sortRoot);
    var sortNotes = sortStore.Load();
    var defaultOrder = treeService.OrderedChildren(sortRoot, sortRoot, sortNotes, sortFolders);
    var tenNote = sortNotes.Single(note => note.Title == "10 노트");
    var twoFolder = sortFolders.Single(path => Path.GetFileName(path) == "2 폴더");
    treeService.Reorder(sortRoot, sortRoot, defaultOrder, tenNote.Path, twoFolder, after: false);
    var reorderedItems = new VaultTreeService(orderStorageDirectory).Build(
        sortRoot,
        sortStore.Load(),
        new VaultTreeService(orderStorageDirectory).LoadFolders(sortRoot),
        new HashSet<string>(StringComparer.OrdinalIgnoreCase));
    if (!reorderedItems.Select(item => item.Name).SequenceEqual(["10 노트", "2 폴더", "10 폴더", "2 노트"])
        || !Directory.EnumerateFiles(orderStorageDirectory, "*.json").Any())
        throw new Exception("탐색기 사용자 지정 정렬 저장 실패");
    var persistedOrder = treeService.OrderedChildren(sortRoot, sortRoot, sortStore.Load(), treeService.LoadFolders(sortRoot));
    treeService.Reorder(sortRoot, sortRoot, persistedOrder, tenNote.Path, twoFolder, after: true);
    var savedTwiceItems = new VaultTreeService(orderStorageDirectory).Build(
        sortRoot,
        sortStore.Load(),
        new VaultTreeService(orderStorageDirectory).LoadFolders(sortRoot),
        new HashSet<string>(StringComparer.OrdinalIgnoreCase));
    if (!savedTwiceItems.Select(item => item.Name).SequenceEqual(["2 폴더", "10 노트", "10 폴더", "2 노트"]))
        throw new Exception("숨김 정렬 파일 재저장 실패");
    var pdfRoot = Path.Combine(root, "PDF 자료 검사");
    Directory.CreateDirectory(pdfRoot);
    var pdfSource = Path.Combine(root, "외부 자료.pdf");
    File.WriteAllText(pdfSource, "%PDF-1.4\n%%EOF");
    var pdfService = new PdfDocumentService(pdfRoot);
    var importedPdf = pdfService.Import(pdfSource, pdfRoot);
    if (!File.Exists(importedPdf.Path) || pdfService.Load().Single().Title != "외부 자료")
        throw new Exception("PDF 저장소 가져오기 및 검색 실패");
    var annotationService = new PdfAnnotationService();
    var savedAnnotation = new PdfAnnotation(
        Guid.NewGuid(), PdfAnnotationKind.Callout, 2,
        .1, .2, .3, .1, .6, .25, "검토 필요", DateTimeOffset.UtcNow);
    annotationService.Save(importedPdf.Path, [savedAnnotation]);
    if (annotationService.Load(importedPdf.Path).Annotations.Single() != savedAnnotation)
        throw new Exception("PDF 비파괴 주석 저장 및 복원 실패");
    var pdfFolder = Directory.CreateDirectory(Path.Combine(pdfRoot, "PDF 폴더")).FullName;
    var movedPdf = pdfService.Move(importedPdf.Path, pdfFolder);
    var renamedPdf = pdfService.Rename(movedPdf.Path, "강의 자료");
    if (File.Exists(PdfAnnotationService.SidecarPath(importedPdf.Path))
        || File.Exists(PdfAnnotationService.SidecarPath(movedPdf.Path))
        || annotationService.Load(renamedPdf.Path).Annotations.Single() != savedAnnotation)
        throw new Exception("PDF 이동·이름 변경 시 주석 동반 이동 실패");
    var pdfFolders = treeService.LoadFolders(pdfRoot);
    var pdfItems = treeService.Build(
        pdfRoot,
        [],
        pdfFolders,
        new HashSet<string>([pdfFolder], StringComparer.OrdinalIgnoreCase),
        pdfs: pdfService.Load());
    var pdfItem = pdfItems.Single(item => item.IsPdf);
    if (pdfItem.Path != renamedPdf.Path || pdfItem.PdfIconOpacity != 1 || pdfItem.NoteDotOpacity != 0)
        throw new Exception("탐색기 PDF 자료 표시 실패");
    var searchedPdfItems = treeService.Build(
        pdfRoot,
        [],
        pdfFolders,
        new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        "강의",
        pdfService.Load());
    if (searchedPdfItems.All(item => !item.IsPdf || item.Path != renamedPdf.Path))
        throw new Exception("탐색기 PDF 이름 검색 실패");
    var textRoot = Path.Combine(root, "TXT 자료 검사");
    Directory.CreateDirectory(textRoot);
    var textService = new TextDocumentService(textRoot);
    var textFolder = Directory.CreateDirectory(Path.Combine(textRoot, "텍스트 폴더")).FullName;
    var text = textService.Create(textFolder, "연결된 텍스트");
    var textBody = "# 텍스트 자료\n\n[[원본]]\n\n끝 공백 보존  \n";
    text = textService.Save(text.Path, textBody);
    if (!text.IsPlainText || text.Body != textBody || text.Metadata.Type != "Text")
        throw new Exception("TXT 직접 편집 내용 및 자료형 보존 실패");
    var renamedText = textService.Rename(text.Path, "이름 바뀐 텍스트");
    var movedText = textService.Move(renamedText.Path, textRoot);
    if (!File.Exists(movedText.Path) || Path.GetDirectoryName(movedText.Path) != textRoot)
        throw new Exception("TXT 이름 변경 및 폴더 이동 실패");
    List<NoteInfo> mixedResources = [.. store.Load(), .. textService.Load()];
    var textLinks = linkService.Build(mixedResources);
    if (!textLinks[movedText.Title].Contains(source.Title, StringComparer.OrdinalIgnoreCase))
        throw new Exception("TXT 위키 링크 인식 실패");
    var textItems = treeService.Build(
        textRoot,
        textService.Load(),
        treeService.LoadFolders(textRoot),
        new HashSet<string>(StringComparer.OrdinalIgnoreCase));
    var textItem = textItems.Single(item => item.Path == movedText.Path);
    if (!textItem.IsText || textItem.TextIconOpacity != 1 || textItem.NoteDotOpacity != 0)
        throw new Exception("탐색기 TXT 자료 표시 실패");
    var networkNotes = store.Load();
    var graphLinks = linkService.Build(networkNotes);
    var graphService = new GraphLayoutService();
    var graphLayout = graphService.Calculate(networkNotes, graphLinks, 720, 1200, source.Title);
    if (!graphLayout.Points.ContainsKey(source.Title) || Math.Abs(graphLayout.Points[source.Title].X - 360) > 1 || Math.Abs(graphLayout.Points[source.Title].Y - 600) > 1) throw new Exception("세로형 그래프 중심 배치 실패");
    var focusedPoint = graphLayout.Points[source.Title];
    if (graphLayout.SelectedNeighbors.Any(title =>
        graphLayout.Points.TryGetValue(title, out var neighbor)
        && Math.Sqrt(Math.Pow(neighbor.X - focusedPoint.X, 2) + Math.Pow(neighbor.Y - focusedPoint.Y, 2)) > 115.01))
        throw new Exception("현재 노트의 1차 방사형 배치 실패");
    var focus = graphLayout.Points[source.Title];
    var directions = graphLayout.Points
        .Where(pair => !pair.Key.Equals(source.Title, StringComparison.OrdinalIgnoreCase))
        .Select(pair => Math.Atan2(pair.Value.Y - focus.Y, pair.Value.X - focus.X))
        .ToList();
    if (directions.Count >= 3)
    {
        var directionalBias = Math.Sqrt(
            Math.Pow(directions.Average(Math.Cos), 2)
            + Math.Pow(directions.Average(Math.Sin), 2));
        if (directionalBias > .36) throw new Exception("선택 노트 기준 그래프 한쪽 쏠림 보정 실패");
    }
    var repeatedLayout = new GraphLayoutService().Calculate(networkNotes, graphLinks, 720, 1200, source.Title);
    if (graphLayout.Points.Any(pair => repeatedLayout.Points[pair.Key] != pair.Value))
        throw new Exception("그래프 배치의 실행 간 결정성 실패");
    graphService.Calculate(networkNotes, graphLinks, 720, 1200, null);
    if (graphService.SimulationRuns != 1) throw new Exception("동일 그래프 레이아웃 캐시 재사용 실패");
    var relationshipDepths = GraphLayoutService.RelationshipDepths("A", new Dictionary<string, List<string>>
    {
        ["A"] = ["B"],
        ["B"] = ["C"],
        ["C"] = ["D"]
    }, 2);
    if (relationshipDepths.GetValueOrDefault("A") != 0
        || relationshipDepths.GetValueOrDefault("B") != 1
        || relationshipDepths.GetValueOrDefault("C") != 2
        || relationshipDepths.ContainsKey("D"))
        throw new Exception("그래프 2차 연관성 강조 범위 계산 실패");
    var radialNotes = new[] { "A", "B", "C", "D", "E", "F", "G" }
        .Select(title => new NoteInfo(title, Path.Combine(root, $"{title}.md"), "", DateTime.Now, NoteMetadata.Manual))
        .ToList();
    var radialLinks = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
    {
        ["A"] = ["B", "C"],
        ["B"] = ["D", "E"],
        ["C"] = ["D", "F"],
        ["F"] = ["G"]
    };
    var radialLayout = new GraphLayoutService().Calculate(radialNotes, radialLinks, 1200, 900, "A");
    if (radialLayout.EgoParents.GetValueOrDefault("B") != "A"
        || radialLayout.EgoParents.GetValueOrDefault("C") != "A"
        || !radialLayout.EgoParents.ContainsKey("D")
        || !radialLayout.EgoParents.ContainsKey("E")
        || !radialLayout.EgoParents.ContainsKey("F")
        || radialLayout.EgoParents.ContainsKey("G"))
        throw new Exception("2단계 방사형 성좌의 대표 부모 선택 실패");
    var radialFocus = radialLayout.Points["A"];
    double RadiusOf(string title) => Math.Sqrt(
        Math.Pow(radialLayout.Points[title].X - radialFocus.X, 2)
        + Math.Pow(radialLayout.Points[title].Y - radialFocus.Y, 2));
    if (new[] { "B", "C" }.Any(title => RadiusOf(title) is < 71.9 or > 126.6)
        || new[] { "D", "E", "F" }.Any(title => RadiusOf(title) < 190)
        || RadiusOf("G") <= RadiusOf("F") + 35)
        throw new Exception("1차·2차 별의 안쪽·바깥쪽 원 분리 실패");
    var treeEdges = radialLayout.EgoParents
        .Select(pair => (From: radialLayout.Points[pair.Value], To: radialLayout.Points[pair.Key], pair.Key, Parent: pair.Value))
        .ToList();
    for (var left = 0; left < treeEdges.Count; left++)
        for (var right = left + 1; right < treeEdges.Count; right++)
        {
            var first = treeEdges[left];
            var second = treeEdges[right];
            if (first.Key == second.Key
                || first.Key == second.Parent
                || first.Parent == second.Key
                || first.Parent == second.Parent)
                continue;
            static double Cross(GraphPoint a, GraphPoint b, GraphPoint c) =>
                (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
            var crossing = Cross(first.From, first.To, second.From) * Cross(first.From, first.To, second.To) < -.001
                && Cross(second.From, second.To, first.From) * Cross(second.From, second.To, first.To) < -.001;
            if (crossing) throw new Exception("2단계 방사형 성좌 뼈대의 연결선 교차 방지 실패");
        }
    var edgeNotes = Enumerable.Range(0, 28)
        .Select(index => new NoteInfo($"edge-{index}", Path.Combine(root, $"edge-{index}.md"), "", DateTime.Now, NoteMetadata.Manual))
        .ToList();
    var edgeLinks = edgeNotes
        .Take(edgeNotes.Count - 1)
        .Select((note, index) => (note.Title, Targets: new List<string> { edgeNotes[index + 1].Title }))
        .ToDictionary(pair => pair.Title, pair => pair.Targets, StringComparer.OrdinalIgnoreCase);
    var edgeLayout = new GraphLayoutService().Calculate(edgeNotes, edgeLinks, 500, 360, edgeNotes[0].Title);
    var boundaryLines = edgeLayout.Points
        .Where(pair => !pair.Key.Equals(edgeNotes[0].Title, StringComparison.OrdinalIgnoreCase))
        .SelectMany(pair => new[]
        {
            (Axis: "x", Value: Math.Round(pair.Value.X, 2), NearEdge: pair.Value.X < 70 || pair.Value.X > 430),
            (Axis: "y", Value: Math.Round(pair.Value.Y, 2), NearEdge: pair.Value.Y < 60 || pair.Value.Y > 300)
        })
        .Where(item => item.NearEdge)
        .GroupBy(item => (item.Axis, item.Value))
        .Select(group => group.Count())
        .DefaultIfEmpty(0)
        .Max();
    if (boundaryLines >= 3)
        throw new Exception("그래프 가장자리 노드의 직선 정렬 방지 실패");
    var zoomedViewport = GraphViewportService.CalculateZoomedViewportOffset(
        new GraphPoint(100, 50),
        new GraphPoint(200, 150),
        1.2,
        new GraphPoint(1440, 960),
        new GraphPoint(400, 300));
    if (Math.Abs(zoomedViewport.X - 160) > .001
        || Math.Abs(zoomedViewport.Y - 90) > .001
        || Math.Abs((zoomedViewport.X + 200) / 1.2 - 300) > .001
        || Math.Abs((zoomedViewport.Y + 150) / 1.2 - 200) > .001)
        throw new Exception("마우스 포인터 기준 그래프 확대 위치 계산 실패");
    var centeredViewport = GraphViewportService.CalculateCenteredZoomedViewportOffset(
        new GraphPoint(0, 0),
        new GraphPoint(450, 350),
        .5,
        1,
        new GraphPoint(1200, 900),
        new GraphPoint(900, 700));
    if (Math.Abs(centeredViewport.X - 150) > .001
        || Math.Abs(centeredViewport.Y - 100) > .001)
        throw new Exception("전체 화면 별 배경의 포인터 중심 확대 위치 계산 실패");
    if (GraphViewportService.ChangeZoom(1, false, 10) != GraphViewportService.MinimumZoom
        || GraphViewportService.ChangeZoom(1, true, 10) != GraphViewportService.MaximumZoom
        || GraphViewportService.LabelMode(.69, false) != GraphLabelMode.FocusOnly
        || GraphViewportService.LabelMode(.7, false) != GraphLabelMode.Orbit
        || GraphViewportService.LabelMode(.3, true) != GraphLabelMode.Orbit
        || GraphViewportService.LabelMode(1.1, false) != GraphLabelMode.Detail)
        throw new Exception("그래프 확대 범위와 단계별 라벨 표시 규칙 실패");
    if (GraphViewportService.NodeRadius(1, false, 1) >= 2.5
        || GraphViewportService.NodeRadius(1, false, 20) > 3
        || GraphViewportService.NodeRadius(1, true, 20) > 5
        || GraphViewportService.NodeRadius(GraphViewportService.MinimumZoom, false, 1) >= .8)
        throw new Exception("그래프 노드 크기 단계 계산 실패");
    if (GraphViewportService.NodeAlpha(20, 1000, 0, false)
            >= GraphViewportService.NodeAlpha(900, 1000, 0, false)
        || GraphViewportService.NodeAlpha(20, 1000, 0, false)
            >= GraphViewportService.NodeAlpha(20, 1000, 8, false)
        || GraphViewportService.NodeAlpha(0, 1, 0, true) != 255)
        throw new Exception("정보량·연결성 기반 실제 별 밝기 계산 실패");
    var zoomedOutField = GraphFieldStarService.Generate(1200, 900, .4);
    var zoomedInField = GraphFieldStarService.Generate(1200, 900, 3.5);
    var repeatedField = GraphFieldStarService.Generate(1200, 900, .4);
    var occupiedRegions = zoomedOutField
        .GroupBy(star => ((int)(star.Position.X / 400), (int)(star.Position.Y / 300)))
        .Select(group => group.Key)
        .ToHashSet();
    if (zoomedOutField.Count <= zoomedInField.Count
        || zoomedOutField.Count < 200
        || !zoomedOutField.SequenceEqual(repeatedField)
        || occupiedRegions.Count != 9
        || zoomedOutField.Count(star => star.Twinkles) < zoomedOutField.Count / 6
        || zoomedOutField.Count(star => star.Twinkles) > zoomedOutField.Count / 4)
        throw new Exception("전역 우주 배경·줌 단계 기반 허수 별 배치 실패");
    var cursorDirections = Enum.GetValues<GraphCursorDirection>();
    for (var index = 0; index < cursorDirections.Length; index++)
    {
        var angle = index * 22.5 * Math.PI / 180;
        var current = new GraphPoint(Math.Cos(angle) * 10, -Math.Sin(angle) * 10);
        if (GraphViewportService.QuantizeCursorDirection(new GraphPoint(0, 0), current) != cursorDirections[index])
            throw new Exception($"그래프 커서 16방향 양자화 실패: {cursorDirections[index]}");
    }
    var labelPlacements = new GraphLabelLayoutService().Arrange(
    [
        new GraphLabelCandidate("현재", new GraphPoint(100, 100), 6, 11, 100, 0, GraphLabelRole.Focus),
        new GraphLabelCandidate("연결 A", new GraphPoint(180, 100), 4, 10, 100, 2, GraphLabelRole.Neighbor),
        new GraphLabelCandidate("연결 B", new GraphPoint(180, 105), 4, 10, 100, 2, GraphLabelRole.Neighbor)
    ], new GraphPoint(100, 100), 300, 220);
    if (labelPlacements.Count < 2
        || labelPlacements.All(placement => placement.Candidate.Role != GraphLabelRole.Focus)
        || labelPlacements.SelectMany((left, index) => labelPlacements.Skip(index + 1).Select(right =>
            left.Position.X < right.Position.X + right.Width + 5
            && left.Position.X + left.Width + 5 > right.Position.X
            && left.Position.Y < right.Position.Y + right.Height + 5
            && left.Position.Y + left.Height + 5 > right.Position.Y)).Any(overlap => overlap))
        throw new Exception("궤도형 그래프 라벨 충돌 회피 실패");
    var lineAwareLabel = new GraphLabelLayoutService().Arrange(
        [new GraphLabelCandidate("선과 겹치면 안 되는 제목", new GraphPoint(100, 100), 4, 10, 100, 0, GraphLabelRole.Neighbor)],
        new GraphPoint(0, 100),
        300,
        220,
        [new GraphLineObstacle(new GraphPoint(105, 100), new GraphPoint(260, 100))]).Single();
    if (lineAwareLabel.Position.X <= 260
        && lineAwareLabel.Position.X + lineAwareLabel.Width >= 105
        && lineAwareLabel.Position.Y <= 103
        && lineAwareLabel.Position.Y + lineAwareLabel.Height >= 97)
        throw new Exception("2단계 연결선과 그래프 라벨 충돌 회피 실패");
    var wrappedLabel = new GraphLabelLayoutService().Arrange(
        [new GraphLabelCandidate("아주 긴 문서 제목도 두 줄로 읽을 수 있어야 한다", new GraphPoint(100, 100), 4, 10, 70, 0, GraphLabelRole.Focus)],
        new GraphPoint(100, 100),
        240,
        180).Single();
    if (wrappedLabel.Width > 70.01 || wrappedLabel.Height < 30)
        throw new Exception("긴 그래프 문서 제목의 두 줄 배치 실패");
    if (!linkService.ExtractTargets("[[연결됨]] [[연결됨|별칭]]").SetEquals(["연결됨"])) throw new Exception("위키 링크 대상 인덱스 생성 실패");
    var rendered = MarkdownPreviewRenderer.Render("""
==강조==와 [[연결됨|위키 링크]] %%숨김%%

| 항목 | 값 |
| --- | --- |
| A | B |

> [!tip] 도움말
> **콜아웃** 본문

- [x] 완료
""", root);
    if (!rendered.Contains("<mark>") || !rendered.Contains("internal-link") || !rendered.Contains("<table") || !rendered.Contains("class=\"callout\"") || !rendered.Contains("md-section") || !rendered.Contains("모두 접기") || !rendered.Contains("workspace-mode-toggle") || rendered.Contains("숨김")) throw new Exception("Markdown 미리보기 렌더링 실패");
    var highlightedCode = MarkdownPreviewRenderer.Render("""
    ```python
    def greet(name):
        # comment
        print("hello", name)
    ```
    """, root);
    TextDiagramChecks.Run(root);
    if (!highlightedCode.Contains("data-language=\"PYTHON\"")
        || !highlightedCode.Contains("tok-keyword\">def")
        || !highlightedCode.Contains("tok-comment\"># comment")
        || !highlightedCode.Contains("tok-string\">&quot;hello&quot;"))
        throw new Exception("오프라인 코드 문법 강조 실패");
    if (!highlightedCode.Contains("event.clipboardData.setData('text/plain', selection.toString())"))
        throw new Exception("코드 블록 주석 포함 순수 텍스트 복사 연결 실패");
    if (!highlightedCode.Contains(".tok-comment{color:#6A9955;font-style:italic;opacity:.82}"))
        throw new Exception("코드 주석 시각적 구분 스타일 실패");
    var lightCode = MarkdownPreviewRenderer.Render("```python\n# light comment\n```", root, surfaceTheme: "light");
    if (!lightCode.Contains("color-scheme:light")
        || !lightCode.Contains("--page:#FFFFFF")
        || !lightCode.Contains(".tok-comment{color:#008000"))
        throw new Exception("라이트 미리보기 테마 적용 실패");

    var confusionMatrix = MarkdownPreviewRenderer.Render("""
## 주요 평가 지표

| 지표 | 공식 | 의미 |
| --- | --- | --- |
| **Accuracy 정확도** | `(TP + TN) / 전체` | 전체 중 맞춘 비율 |
| **Precision 정밀도** | `TP / (TP + FP)` | 양성 예측 중 진짜 양성 |

### 핵심 암기

```text
Precision = TP / (TP + FP)
Recall = TP / (TP + FN)
```

- **TP (True Positive)**: 실제 양성 → 양성 예측
""", root);
    if (!confusionMatrix.Contains("<h2") || !confusionMatrix.Contains("<h3") || !confusionMatrix.Contains("<table") || !confusionMatrix.Contains("<strong>") || !confusionMatrix.Contains("<pre") || !confusionMatrix.Contains("<code")) throw new Exception("ChatGPT 스타일 문서 렌더링 실패");
    if (!confusionMatrix.Contains("width:max-content;max-width:100%") || confusionMatrix.Contains("table{border-collapse:collapse;width:100%")) throw new Exception("내용 기반 표 너비 적용 실패");

    var windowsTextBoxMarkdown = "## CR 줄바꿈\r\r| 항목 | 값 |\r| --- | --- |\r| TP | True Positive |\r\r- **목록** 항목";
    var normalizedRender = MarkdownPreviewRenderer.Render(windowsTextBoxMarkdown, root);
    if (!normalizedRender.Contains("<h2") || !normalizedRender.Contains("<table") || !normalizedRender.Contains("<ul") || !normalizedRender.Contains("<strong>")) throw new Exception("WinUI CR 줄바꿈 렌더링 실패");

    var visibleLineBreakRender = MarkdownPreviewRenderer.Render("첫 줄\n둘째 줄", root);
    if (!visibleLineBreakRender.Contains("첫 줄<br") || !visibleLineBreakRender.Contains("둘째 줄")) throw new Exception("일반 본문 단일 줄바꿈 표시 실패");
    if (MarkdownText.OriginalOffsetFromNormalized("첫 줄\r\n둘째 줄", 4) != 5
        || MarkdownText.OriginalOffsetFromNormalized("첫 줄\r\n둘째 줄", 8) != 9)
        throw new Exception("미리보기 원문 위치의 CRLF 보정 실패");

    var promotedHeading = MarkdownHeadingLevelService.Change("## 제목", 5, 0, -1);
    if (!promotedHeading.Changed || promotedHeading.Text != "# 제목" || promotedHeading.SelectionStart != 4) throw new Exception("제목 한 수준 증가 단축키 처리 실패");
    var demotedHeadings = MarkdownHeadingLevelService.Change("## 첫째\n본문\n### 둘째", 0, 17, 1);
    if (!demotedHeadings.Changed || demotedHeadings.Text != "### 첫째\n본문\n#### 둘째") throw new Exception("선택 제목 한 수준 감소 단축키 처리 실패");
    if (MarkdownHeadingLevelService.Change("# 최대", 0, 0, -1).Changed || MarkdownHeadingLevelService.Change("###### 최소", 0, 0, 1).Changed) throw new Exception("제목 수준 변경 범위 제한 실패");

    var attachmentDirectory = Path.Combine(root, "attachments");
    Directory.CreateDirectory(attachmentDirectory);
    File.WriteAllBytes(Path.Combine(attachmentDirectory, "붙여넣기.png"), [0x89, 0x50, 0x4e, 0x47]);
    var imageRender = MarkdownPreviewRenderer.Render("![[attachments/붙여넣기.png]]", root);
    if (!imageRender.Contains("class=\"internal-image\"")
        || !imageRender.Contains($"src=\"https://{MarkdownPreviewRenderer.VaultAssetHostName}/attachments/"))
        throw new Exception("붙여넣은 이미지 렌더링 실패");
    if (imageRender.Contains(";base64,", StringComparison.OrdinalIgnoreCase))
        throw new Exception("이미지가 HTML에 포함되어 WebView2 문서 크기를 키움");
    var sizedImageRender = MarkdownPreviewRenderer.Render("![[attachments/붙여넣기.png|480]]\n\n![[attachments/붙여넣기.png|640x360]]", root);
    if (!sizedImageRender.Contains("width=\"480\"")
        || !sizedImageRender.Contains("width=\"640\" height=\"360\""))
        throw new Exception("이미지 크기 문법 렌더링 실패");
    var invalidImageSizeRender = MarkdownPreviewRenderer.Render("![[attachments/붙여넣기.png|640x0]]", root);
    if (invalidImageSizeRender.Contains("width=\"640\"") || invalidImageSizeRender.Contains("height=\"0\""))
        throw new Exception("잘못된 이미지 크기 문법 제한 실패");
    if (!PdfReaderRules.IsPdf("자료.PDF") || PdfReaderRules.IsPdf("자료.md"))
        throw new Exception("PDF 파일 판별 실패");
    var pdfUri = PdfReaderRules.FileUri(Path.Combine(root, "한글 자료 & 100%.pdf"));
    if (!pdfUri.IsFile || !Uri.UnescapeDataString(pdfUri.LocalPath).EndsWith("한글 자료 & 100%.pdf", StringComparison.Ordinal))
        throw new Exception("PDF 로컬 파일 URI 변환 실패");

    var crNote = store.Create("# 제목 정규화");
    crNote = store.Save(crNote.Path, crNote.Title, windowsTextBoxMarkdown, NoteMetadata.Manual);
    var crReloaded = store.Load().Single(note => note.Path == crNote.Path);
    if (crReloaded.Title != "제목 정규화" || crReloaded.Body.Contains('\r') || !crReloaded.Body.Contains("\n\n| 항목")) throw new Exception("Markdown 저장 줄바꿈 정규화 실패");

    var headingNote = store.Create("제목 보존 검사");
    headingNote = store.Save(headingNote.Path, headingNote.Title, "# 첫 본문 제목\n\n내용\n\n# 둘째 본문 제목\n\n마지막", NoteMetadata.Manual, headingNote.Title);
    var headingReloaded = store.Load().Single(note => note.Path == headingNote.Path);
    if (!headingReloaded.Body.StartsWith("# 첫 본문 제목", StringComparison.Ordinal)
        || !headingReloaded.Body.Contains("# 둘째 본문 제목", StringComparison.Ordinal))
        throw new Exception("본문 1단계 제목 재로드 보존 실패");

    var sectionMarkdown = "## 첫 구역\n\n첫 본문\n\n### 하위 구역\n\n하위 본문\n\n## 둘째 구역\n\n둘째 본문";
    var sectionRender = MarkdownPreviewRenderer.Render(sectionMarkdown, root);
    if (!sectionRender.Contains("data-level='1']>.md-summary>h1{font-size:calc(13.3px * var(--font-scale))") || sectionRender.Contains("font-size:19px")) throw new Exception("노트 제목보다 작은 본문 1단계 제목 크기 적용 실패");
    var themedRender = MarkdownPreviewRenderer.Render(sectionMarkdown, root, initialScrollY: 0, fontScale: 1.2, accentColor: "#6CB6FF");
    if (!themedRender.Contains(":root{--font-scale:1.2;--accent:#6CB6FF;")
        || !themedRender.Contains("color:var(--accent)"))
        throw new Exception("미리보기 글자 크기와 강조색 설정 적용 실패");
    if (!sectionRender.Contains("document.addEventListener('click'")
        || !sectionRender.Contains("type: 'focus-editor', offset")
        || !sectionRender.Contains("type: 'hover-editor', offset, endOffset")
        || !sectionRender.Contains("type: 'hover-editor-clear'")
        || !sectionRender.Contains("data-source-offset=\"0\"")
        || !sectionRender.Contains("document.addEventListener('dblclick'"))
        throw new Exception("미리보기 클릭 원문 위치 연결 실패");
    var inlineSource = "## 제목\r\n\r\n반복 **굵게** 그리고 *기울임*\r\n\r\n반복\r\n\r\n- 목록\r\n\r\n[링크](https://example.com)\r\n\r\n```text\r\n코드\r\n```\r\n\r\n수식 $x+y$\r\n\r\n> [!note] 제목\r\n> 콜아웃";
    var inlineSession = new PreviewEditSession(inlineSource);
    var inlineRender = MarkdownPreviewRenderer.Render(inlineSource, root, editSession: inlineSession);
    var editMatches = System.Text.RegularExpressions.Regex.Matches(inlineRender, "<span class=\"editable-text\" data-edit-start=\"(\\d+)\"[^>]*>(.*?)</span>");
    var editable = editMatches.ToDictionary(match => int.Parse(match.Groups[1].Value), match => System.Net.WebUtility.HtmlDecode(match.Groups[2].Value));
    var normalizedInline = MarkdownText.NormalizeNewlines(inlineSource);
    foreach (var label in new[] { "제목", "굵게", "기울임", "목록" })
        if (!editable.TryGetValue(normalizedInline.IndexOf(label, StringComparison.Ordinal), out var value) || value != label)
            throw new Exception($"인라인 원문 범위 연결 실패: {label}");
    if (editable.Values.Any(value => value.Contains("링크") || value.Contains("코드") || value.Contains("수식") || value.Contains("콜아웃")))
        throw new Exception("인라인 제외 영역 보호 실패");
    if (editable.Values.Count(value => value.Trim() == "반복") != 2)
        throw new Exception("인라인 반복 텍스트 위치 구분 실패");
    System.Text.Json.JsonElement EditMessage(PreviewEditSession session, int start, string text) =>
        System.Text.Json.JsonSerializer.SerializeToElement(new { session = session.Id, start, text });
    var boldStart = normalizedInline.IndexOf("굵게", StringComparison.Ordinal);
    if (!inlineSession.TryApply(inlineSource, EditMessage(inlineSession, boldStart, "한글 😀 *별* [링크]"), out var inlineUpdated)
        || !inlineUpdated.Contains("**한글 😀 \\*별\\* \\[링크\\]**") || !inlineUpdated.Contains("\r\n"))
        throw new Exception("인라인 서식 보존 및 CRLF 원문 수정 실패");
    if (inlineSession.TryApply(inlineSource + "변경", EditMessage(inlineSession, boldStart, "수정"), out _)
        || inlineSession.TryApply(inlineSource, EditMessage(new PreviewEditSession(inlineSource), boldStart, "수정"), out _)
        || inlineSession.TryApply(inlineSource, EditMessage(inlineSession, 0, "수정"), out _)
        || inlineSession.TryApply(inlineSource, EditMessage(inlineSession, boldStart, "줄\n변경"), out _)
        || inlineSession.TryApply(inlineSource, EditMessage(inlineSession, boldStart, " "), out _))
        throw new Exception("오래된 미리보기·허용하지 않은 범위·구조 변경 차단 실패");
    var secondSession = new PreviewEditSession(inlineUpdated);
    var secondRender = MarkdownPreviewRenderer.Render(inlineUpdated, root, editSession: secondSession);
    if (!secondRender.Contains("한글 😀 *별* [링크]</span>"))
        throw new Exception("인라인 이스케이프 텍스트 재편집 연결 실패: " + System.Text.RegularExpressions.Regex.Match(secondRender, "<strong>.*?</strong>").Value);
    if (MarkdownPreviewRenderer.Render(inlineSource, root).Contains("<span class=\"editable-text\""))
        throw new Exception("읽기 전용 미리보기 편집 금지 실패");
    var transformedSource = "[[링크]]\n\n> [!note] 제목\n> 본문\n\n%%숨김%%\n\n뒤 문장\n\n| 항목 | 값 |\n| --- | --- |\n| 표 셀 | 1 |";
    var transformedSession = new PreviewEditSession(transformedSource);
    var transformedRender = MarkdownPreviewRenderer.Render(transformedSource, root, editSession: transformedSession);
    var afterTransformStart = transformedSource.IndexOf("뒤 문장", StringComparison.Ordinal);
    if (!transformedSession.TryApply(transformedSource, EditMessage(transformedSession, afterTransformStart, "수정"), out var afterTransformResult)
        || afterTransformResult != transformedSource.Remove(afterTransformStart, "뒤 문장".Length).Insert(afterTransformStart, "수정"))
        throw new Exception("전처리 후 인라인 위치 연결 실패");
    if (System.Text.RegularExpressions.Regex.IsMatch(transformedRender, "<span class=\"editable-text\"[^>]*>[^<]*표 셀"))
        throw new Exception("복합 표 구조 인라인 편집 제외 실패");
    var repeatedSourceRender = MarkdownPreviewRenderer.Render("같은 문장\n\n같은 문장", root);
    if (!repeatedSourceRender.Contains("data-source-offset=\"0\"")
        || !repeatedSourceRender.Contains("data-source-offset=\"7\""))
        throw new Exception("반복 문단 원문 위치 구분 실패");
    var calloutSourceRender = MarkdownPreviewRenderer.Render("앞\n\n> [!note] 제목\n> 본문", root);
    if (!calloutSourceRender.Contains("class=\"callout\" data-source-offset=\"3\""))
        throw new Exception("콜아웃 원문 위치 연결 실패");
    if (!sectionRender.Contains("if (sectionCount)")) throw new Exception("제목 없는 노트의 미리보기 상호작용 연결 실패");
    if (!sectionRender.Contains("heading.title = '클릭해서 접기 또는 펼치기'") || sectionRender.Contains("update-section") || sectionRender.Contains("section-editor") || sectionRender.Contains("beginEditing")) throw new Exception("수준별 편집 제거 및 제목 접기 연결 실패");
    if (!sectionRender.Contains("initialFoldStates[foldKey] : true")) throw new Exception("제목 구역 기본 펼침 상태 적용 실패");
    if (sectionRender.Contains("exclusiveSections")
        || sectionRender.Contains("details.parentElement?.children")
        || !sectionRender.Contains("document.querySelectorAll('.md-section').forEach(section => section.open = true)"))
        throw new Exception("본문 제목의 독립 접기와 모두 펼치기 복원 실패");

    var folderExpansionService = new FolderExpansionService();
    var expansionRoot = Path.Combine(root, "accordion");
    var expansionFolders = new[]
    {
        expansionRoot,
        Path.Combine(expansionRoot, "A"),
        Path.Combine(expansionRoot, "B"),
        Path.Combine(expansionRoot, "A", "A1"),
        Path.Combine(expansionRoot, "A", "A2")
    };
    var explorerFolders = expansionFolders.Append(BuiltInGuideService.FolderPath).ToArray();
    var expandedFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    folderExpansionService.InitializeDefaults(expansionRoot, explorerFolders, expandedFolders);
    if (expandedFolders.Count(folder => Path.GetDirectoryName(folder)?.Equals(expansionRoot, StringComparison.OrdinalIgnoreCase) == true) != 1)
        throw new Exception("탐색기 형제 폴더 기본 상호 배타 상태 실패");
    folderExpansionService.ExpandExclusive(expansionRoot, explorerFolders, expandedFolders, BuiltInGuideService.FolderPath);
    if (!expandedFolders.Contains(BuiltInGuideService.FolderPath) || expandedFolders.Contains(expansionFolders[1]) || expandedFolders.Contains(expansionFolders[2]))
        throw new Exception("가상 안내 폴더의 최상위 아코디언 참여 실패");
    folderExpansionService.ExpandExclusive(expansionRoot, explorerFolders, expandedFolders, expansionFolders[2]);
    if (expandedFolders.Contains(BuiltInGuideService.FolderPath))
        throw new Exception("실제 폴더 펼침 시 가상 안내 폴더 접기 실패");
    if (!expandedFolders.Contains(expansionFolders[2]) || expandedFolders.Contains(expansionFolders[1]))
        throw new Exception("탐색기 같은 수준 폴더 상호 배타 펼침 실패");
    folderExpansionService.ExpandExclusive(expansionRoot, explorerFolders, expandedFolders, expansionFolders[4]);
    if (!expandedFolders.Contains(expansionFolders[4]) || expandedFolders.Contains(expansionFolders[3]))
        throw new Exception("탐색기 하위 폴더 독립 그룹 처리 실패");
    expandedFolders.Add(expansionFolders[3]);
    folderExpansionService.EnforceExclusive(
        expansionRoot,
        explorerFolders,
        expandedFolders,
        [expansionFolders[4]]);
    if (!expandedFolders.Contains(expansionFolders[4]) || expandedFolders.Contains(expansionFolders[3]))
        throw new Exception("탐색기 중복 펼침 상태 자동 복구 실패");

    var guideService = new BuiltInGuideService();
    var guideItems = guideService.BuildItems(
        new HashSet<string>([BuiltInGuideService.FolderPath], StringComparer.OrdinalIgnoreCase),
        null);
    if (guideItems.Count < 5
        || guideItems[0] is not { IsFolder: true, IsVirtual: true }
        || guideItems[0].Depth != 0
        || guideItems.Skip(1).Any(item => item.Depth != 1)
        || guideItems.Skip(1).Any(item => item.Note is not { IsReadOnly: true })
        || guideService.FindByTitle("마크다운 사용법") is null)
        throw new Exception("읽기 전용 Asterism 안내 문서 구성 실패");

    var rememberedFoldRender = MarkdownPreviewRenderer.Render("# Section\n\nBody", root, null, new Dictionary<string, bool> { ["1:Section#1"] = false });
    if (!rememberedFoldRender.Contains("\"1:Section#1\":false")
        || !rememberedFoldRender.Contains("type: 'fold-state'")
        || !rememberedFoldRender.Contains("Object.hasOwn(initialFoldStates, foldKey)"))
        throw new Exception("미리보기 갱신 시 제목 접힘 상태 복원 실패");

    var rememberedScrollRender = MarkdownPreviewRenderer.Render("# Section\n\nBody", root, null, null, 321.5);
    if (!rememberedScrollRender.Contains("const initialScrollY = 321.5")
        || !rememberedScrollRender.Contains("window.scrollTo(0, initialScrollY)")
        || !rememberedScrollRender.Contains("type: 'preview-scroll'")
        || !rememberedScrollRender.Contains("document.documentElement.scrollHeight - window.innerHeight")
        || !rememberedScrollRender.Contains("y: window.scrollY, maxY")
        || !rememberedScrollRender.Contains("*::-webkit-scrollbar{width:8px;height:8px}")
        || !rememberedScrollRender.Contains("scrollbar-color:#555555 transparent"))
        throw new Exception("미리보기 갱신 시 스크롤 위치 복원 실패");

    var mathRender = MarkdownPreviewRenderer.Render("인라인 $\\sum_{i=1}^{n} x_i$\n\n$$\n\\frac{1}{n} \\sum_{i=1}^{n} x_i\n$$", root);
    if (!mathRender.Contains("<span class=\"math\"")
        || !System.Text.RegularExpressions.Regex.IsMatch(mathRender, "<div[^>]*class=\\\"[^\\\"]*math")
        || !mathRender.Contains("https://node-assets.local/katex.min.js")
        || !mathRender.Contains("https://node-assets.local/auto-render.min.js")
        || !mathRender.Contains("window.renderMathInElement(root"))
        throw new Exception("LaTeX 수식 렌더링 연결 실패");
    var headingMathRender = MarkdownPreviewRenderer.Render("# 평균 $\\bar{x}$\n\n## 분산 $s^2$", root);
    var firstHeadingHtml = System.Text.RegularExpressions.Regex.Match(headingMathRender, "<h1[^>]*>(.*?)</h1>", System.Text.RegularExpressions.RegexOptions.Singleline).Groups[1].Value;
    var secondHeadingHtml = System.Text.RegularExpressions.Regex.Match(headingMathRender, "<h2[^>]*>(.*?)</h2>", System.Text.RegularExpressions.RegexOptions.Singleline).Groups[1].Value;
    if (!firstHeadingHtml.Contains("class=\"math\"") || !secondHeadingHtml.Contains("class=\"math\""))
        throw new Exception($"마크다운 제목 수식 렌더링 실패: h1={firstHeadingHtml}, h2={secondHeadingHtml}");
    var noteTitleMathRender = MarkdownPreviewRenderer.RenderTitle("평균 $\\bar{x}$ <script>", 1.2, "dark");
    if (!noteTitleMathRender.Contains("평균 $\\bar{x}$ &lt;script&gt;")
        || !noteTitleMathRender.Contains("renderMathInElement(document.getElementById('title')")
        || noteTitleMathRender.Contains("<script></div>"))
        throw new Exception("노트 제목 수식 렌더링 및 HTML 보호 실패");

    var chatGptMathRender = MarkdownPreviewRenderer.Render("인라인 \\(x + y\\)\n\n\\[\n\\sum_{i=1}^{n} x_i\n\\]", root);
    if (!chatGptMathRender.Contains("<span class=\"math\">\\(x + y\\)</span>")
        || !System.Text.RegularExpressions.Regex.IsMatch(chatGptMathRender, "<div[^>]*class=\\\"[^\\\"]*math")
        || !chatGptMathRender.Contains("\\sum_{i=1}^{n} x_i"))
        throw new Exception("ChatGPT 스타일 LaTeX 구분자 보존 실패");
    if (!UpdateService.TryParseVersion("v0.1.0", out var parsedVersion) || parsedVersion != new Version(0, 1, 0) || UpdateService.TryParseVersion("latest", out _)) throw new Exception("업데이트 버전 분석 실패");
    Console.WriteLine("Asterism checks passed.");
}
finally { Directory.Delete(root, true); }
