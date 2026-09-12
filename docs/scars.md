<!-- yazan: codex · gpt-6 -->
# Yara Durumu — per-scar test tablosu

Ölçüm: `dotnet test Oom.sln -c Release` — Toplam yara sayısı: 80, Geçti: 80, Başarısız: 0 (paralellik depodaki `xunit.runner.json` ile kapalı). Tarih: 2026-09-12, `sade/2026-09-12` dalı.

Bu defter testlerin `DisplayName`'lerinden üretilir ve Y-125 bağı iki yönlüdür: her testin bir satırı, her satırın bir testi vardır. 2.2 sadeleştirmesinde silinen davranışların yaraları defterden düşmüştür — kota, bench, kullanıcı kapsamı kurulum, v0 göçü, şema merdiveni, Ollama arka ucu, `calls` defteri, kanca anı enjeksiyonu, sır/KVK maskeleme ve toast. Düşen satırlar git tarihinde durur.

| Y-# | Sınıf | Yara | Test | Durum |
|---|---|---|---|---|
| Y-001 | yazma-yolu | Sınırlandırılmış flush yalnız son 30 turu/karakter tavanını yolladı ama imleci `turns_seen`'e atladı: `ddd44fc9`… | YazmaYoluScars.Y001_BoundedFlushCoversEveryTurnExactlyOnce | yeşil |
| Y-002 | yazma-yolu | PreCompact aynı imleç yolundan bütün öneki düşürüyor; test yorumu bunu bilerek belgeliyor (`test_flush_teslimat.py:365`) | YazmaYoluScars.Y002_PreCompactCommitsOnlyProcessedRange | yeşil |
| Y-003 | yazma-yolu | Desktop ana oturumları SessionEnd üretmiyor: 29 Ağustos sonrası 27 ölçülebilir oturumun 13'ü (%48) hiç daily'ye girmedi… | YazmaYoluScars.Y003_SweepCoversSessionsWithoutHooks | yeşil |
| Y-004 | yazma-yolu | Ayrık PowerShell→Python teslimatı sonucunu kanıtlamıyor: `flush-launch.ps1:150` pid/çıkış/stderr almadan `Start… | YazmaYoluScars.Y004_IngressWithoutTerminalOutcomeBecomesOverdue | yeşil |
| Y-005 | yazma-yolu | Yakalama kayıplı ve geri dönüşsüz: yalnız user/assistant **metni** saklanıyor (son 30 tur / 15k karakter), araç… | YazmaYoluScars.Y005_RawTranscriptRoundTripsWithoutLoss | yeşil |
| Y-006 | yazma-yolu | Tarama kendi mekanizmasının transkriptlerini insan oturumu sandı: 15 alt-ajan + 2 `stage-compile` transkripti daily'ye… | YazmaYoluScars.Y006_MechanismTranscriptsAreExcluded | yeşil |
| Y-007 | yazma-yolu | `--since-hours` penceresi hiç görülmemiş transkriptlere de uygulandı: geç koşan bir tarama, daha önce hiç görmediği… | YazmaYoluScars.Y007_UnstampedOldTranscriptIsProcessed | yeşil |
| Y-008 | yazma-yolu | Tarama kayıtları taramanın kendi saatiyle damgalandı: 6 Eylül 15:25 oturumu `daily/2026-09-07.md` içine "02:00" olarak… | YazmaYoluScars.Y008_EventTimeComesFromLastTurn | yeşil |
| Y-010 | yazma-yolu | Task-notification gövdeleri ve araç anlatıları kullanıcı metni gibi flush'a girdi; 8 Eyl daily'sinin 16:44 bloğu makine… | YazmaYoluScars.Y010_MachineEnvelopeIsNotSummarized | yeşil |
| Y-011 | yazma-yolu | `claude -p` kullanıcının plan modunu miras aldı ve `<ExitPlanMode/>` ile sohbet etti: **110 flush'ın 23'ü (%21)… | YazmaYoluScars.Y011_ClaudeRunnerIsIsolatedAndFallsBack | yeşil |
| Y-012 | yazma-yolu | Reddedilen özet kayboluyordu ve kaynağını tüketiyordu: reddedilen/boş derleyici çıktısı geldiği daily'yi "işlendi" saydı | YazmaYoluScars.Y012_RejectedSummaryDoesNotAdvanceCursor | yeşil |
| Y-013 | yazma-yolu | 13 `missing-transcript`: kanca payload'ındaki yol taramada bulunamadı | YazmaYoluScars.Y013_MissingTranscriptIsRelocatedBySessionId | yeşil |
| Y-014 | yazma-yolu | Canlı vault'ta flush'ta günlük-dosya kilidi yoktu (repo kopyasında vardı) → eşzamanlı yazmada blok kaybı | YazmaYoluScars.Y014_ConcurrentDailyAppendsLoseNoBlock | yeşil |
| Y-015 | yazma-yolu | Yerleşme penceresi kontrolü (`current - mtime < fresh_seconds`) pencere `0` (kapalı) iken bile saatin önünde `mtime`… | YazmaYoluScars.Y015_ZeroFreshWindowDisablesFiltering | yeşil |
| Y-016 | özetleyici | Yerel qwen3:8b üretim penceresinde **%14,5 uydurma** üretti (30B %8,8 · 8B sıkı sözleşme %8,8 · Haiku %5,9); 17,6k… | OzetleyiciScars.Y016_UnmeasuredFallbackCannotEnterNormalConfidence | yeşil |
| Y-017 | özetleyici | Özet doğrulayıcı sorunsuz özetleri reddetti: 8 Eyl taramasında 20 Haiku özetinin 6'sı `summary-schema-invalid` diye… | OzetleyiciScars.Y017_SummaryValidatorNormalizesNoiseAndPersistsRejection | yeşil |
| Y-018 | özetleyici | Sabit model takma adları kullanılıyordu (`haiku` takma adı Sonnet'e çözümleniyordu) — çağrılan modelin kim olduğu… | OzetleyiciScars.Y018_ModelIdMustBeExactAndRecordedFromResponse | yeşil |
| Y-022 | derleyici | Yanlış kavramın belirleyici düzeltme yolu yok: `confidence`/`superseded_by` alanları yok, transkripte karşı hiçbir… | DerleyiciScars.Y022_CorrectionInvalidatesStaleClaimBeforeQuery | yeşil |
| Y-023 | derleyici | Silinmiş alt-ajan sağlayıcıları altı canlı kavramda duruyor: **84 hayalet çapa**; derleyici değişen her kavrama… | DerleyiciScars.Y023_RetiredAnchorNeverReturns | yeşil |
| Y-024 | derleyici | Emekliye ayrılan hayalet çapa geçmişi hâlâ **aranabilir metindi**: `<!-- gecmis-capalar: ... -->` yorumu… | DerleyiciScars.Y024_RetiredAnchorCommentIsNotSearchable | yeşil |
| Y-025 | derleyici | 400 satırlık sicil tavanı 534 kavramlık korpusta kaçınılmaz olarak eksik duplicate/update görünümü üretiyor (`71/519`… | DerleyiciScars.Y025_CandidateSelectionSeesFullCorpus | yeşil |
| Y-026 | derleyici | Araçlı derleme = karantinasız ajan; satır-başı direktif regex'i kendi girdisini göremiyor; MCP/context-pack çıktısında… | DerleyiciScars.Y026_DirectiveInputAndUnsafePathsAreRejected | yeşil |
| Y-027 | derleyici | `DIRECTIVE_SHAPED` yalnız uyarı yazıyordu, **terfiyi durdurmuyordu** → enjeksiyon içeriği kalıcı nota dönüşebiliyordu | DerleyiciScars.Y027_DirectiveFindingStopsPromotion | yeşil |
| Y-028 | derleyici | Alt klasördeki not görünmezdi: derleyici alt klasöre yazmaya izin verirken indeks ve kök harita `glob("*.md")` ile… | DerleyiciScars.Y028_NestedConceptPathRejectsWholeRun | yeşil |
| Y-029 | derleyici | Yayın işlem-bütünlüklü değildi (sıralı `replace`); kök harita ve arama indeksi, daily "işlendi" işaretlenmeden önce… | DerleyiciScars.Y029_PublicationIsAtomicAndIndexPrecedesConsumption | yeşil |
| Y-030 | derleyici | Yeniden kurulum (rebuild) hataları, kaynak tüketildikten **sonra** uyarıya indiriliyordu | DerleyiciScars.Y030_RebuildFailureLeavesDailyPending | yeşil |
| Y-031 | derleyici | Sağlayıcı yok: 527 notun 500'ünde oturum çapası yok; 264 oturumun 248'i çapasız | DerleyiciScars.Y031_ImportedSessionAnchorCoverageIsAtLeastNinetyEightPercent | yeşil |
| Y-032 | derleyici | `index.md` 494 satır iken korpusta 496 kavram vardı — indekste olmayan iki kavram sessizce dışarıdaydı | DerleyiciScars.Y032_VerifyFailsForMissingOrExtraConcepts | yeşil |
| Y-034 | derleyici | `warn:registry-truncated` başarılı bir seçim telemetrisi olduğu hâlde uyarı sayılıyordu ve gerçek uyarıları gürültüde… | DerleyiciScars.Y034_TelemetryDoesNotHidePendingDailies | yeşil |
| Y-035 | getirme | El katmanındaki güncel düzeltmeler indeks nüfusunun dışındaydı (`retrieve.py:612-614` yalnız `knowledge/concepts`)… | GetirmeScars.Y035_CorrectionLayerOutranksStaleConcept | yeşil |
| Y-040 | getirme | BM25 alan ağırlıkları hiç uygulanmıyordu: `name UNINDEXED` sütunu ağırlıkların ilkini (8'i) yutuyordu, fiili ağırlıklar… | GetirmeScars.Y040_Bm25WeightsIncludeUnindexedLeadingZero | yeşil |
| Y-041 | getirme | Tazelik çarpanı ölçek-körü biçimde füzyon skorunu domine ediyordu (%1,6'lık bant 4× ile çarpılıyordu) ve güçlü… | GetirmeScars.Y041_UnknownRetrievalModeFails | yeşil |
| Y-044 | getirme | Türkçe katlama `ToLowerInvariant` ile yanlış: `I→ı`, `İ→i` dönüşümü özel tablo ister; ham `unicode61` tokenizer'da İ/ı… | GetirmeScars.Y044_TurkishFoldHandlesDottedAndDotlessI | yeşil |
| Y-045 | kanca | Açılış bağlamı çoğunlukla sabit ~4,2–4,8k jetonluk vergi: 87 başlangıcın ortalaması **15.134 karakter**, %70,66'sı… | KancaScars.Y045_ContextKeepsCurrentDataWithinCap | yeşil |
| Y-046 | kanca | SessionStart kancası aynı saniyede iki kez ateşledi (87 enjeksiyon kaydının 16'sı çift `ts`): Desktop'ın açtığı… | KancaScars.Y046_HelpersAreDedupedBySessionAndEventIdentity | yeşil |
| Y-047 | kanca | Uzun Windows oturumlarında kancalar ~2,5 saniye sonra sessizce duruyor (üst-akım #16047); resmî doküman SessionEnd'i… | KancaScars.Y047_SystemWorksWhenAllHooksAreSilent | yeşil |
| Y-048 | kanca | Kanca sayaçları oturumlar arasında paylaşımlıydı (`prompt_count` / `session_start_time` tek dosyada) | KancaScars.Y048_HookCountersArePerSession | yeşil |
| Y-049 | kanca | Python yoksa kancalar sessizce "başarılı" oluyordu | KancaScars.Y049_HookFailureIsPersisted | yeşil |
| Y-050 | kanca | El katmanının tek mekanik denetimi (`session-end.ps1` içindeki `needs_reflection`) SessionEnd'e bağlıydı — yani tam da… | KancaScars.Y050_CompanionAuditUsesContentNotMtime | yeşil |
| Y-051 | kanca | `CLAUDE.md`'nin enjeksiyon listesi bayattı (Kurallar ve Journal eksikti); Kurallar (2.866 B) ve Journal (406 B) her… | KancaScars.Y051_ContextSectionsMatchDocumentedList | yeşil |
| Y-058 | durum-deposu | Harcama defteri her yanıtı iki-üç kez saydı: gerçek arşivde (1.740 dosya) **50.596 ham `usage` satırı 22.036 farklı… | DurumDeposuScars.Y058_UsageIsDeduplicatedByMessageId | yeşil |
| Y-059 | durum-deposu | Harcama defteri Master oturumlarıyla alt ajanları ayırmıyor: 6 Eylül Claude cache-read'inin **50,8M'i, yani %31,8'i**… | DurumDeposuScars.Y059_OwnerClassesSumToDeduplicatedTotal | yeşil |
| Y-060 | durum-deposu | Günlük toplamlar oturumun **son** zaman damgasına anahtarlanıyordu: gece yarısını aşan bir oturumun tüm harcaması… | DurumDeposuScars.Y060_UsageIsAssignedByResponseTimestamp | yeşil |
| Y-061 | durum-deposu | Durum dosyaları sınırsız büyüdü: `harcama-defteri.json` **4,5 MB**'a şişti; `enjeksiyon.jsonl`, getirme defterleri ve… | DurumDeposuScars.Y061_StateRetentionBoundsOneYearDatabase | yeşil |
| Y-063 | durum-deposu | `health`/ledger yazımları kilitsizdi ve `health` eski uyarıları temizlemiyordu | DurumDeposuScars.Y063_HealthWritesAreLockedAndWarningsAge | yeşil |
| Y-064 | durum-deposu | `_atomic_write_json` geçici dosya adı yalnız pid taşıyordu → tek süreç içindeki şerit iş parçacıkları aynı geçici… | DurumDeposuScars.Y064_AtomicWriteUsesUniqueTemporaryNamesAndRetries | yeşil |
| Y-075 | test-disiplini | Kalite kapıları gerçek doğruluğu ölçmüyordu: `containment` örneklemi kaynak metinde bulunan **yanlış** iddiayı… | TestDisipliniScars.Y075_AccuracyAxesAreReportedSeparately | yeşil |
| Y-077 | test-disiplini | Bir test iki PowerShell kanca başlangıcının **aynı duvar-saati saniyesine** düşmesine dayanıyordu ve CI'da düştü | TestDisipliniScars.Y077_TimingTestUsesInjectedClock | yeşil |
| Y-078 | test-disiplini | Tarih-bombalı 2 CI testi vardı; `durum`'un çağrı özeti gerçek saat dönümünde kırılıyordu | TestDisipliniScars.Y078_DateBoundaryUsesFakeNow | yeşil |
| Y-079 | test-disiplini | `pytest ... \ | TestDisipliniScars.Y079_CiPreservesTestExitCodeWithoutPipelineMasking | yeşil |
| Y-081 | test-disiplini | Çağrı yeri amaç taraması çalışan bir checkout'ta düştü (depo geneli tarama, kendi test verisini de tarıyordu) | TestDisipliniScars.Y081_StaticScanExcludesFixturesOnly | yeşil |
| Y-082 | test-disiplini | Kabul ölçümü tek eksenliydi: Faz 1'in epizodik kazancı gold set'i düşürdü ve bu ancak kabul koşumunda görüldü (bkz. Y… | TestDisipliniScars.Y082_AcceptanceGateRequiresPrimaryAndCounterMetric | yeşil |
| Y-085 | süreç-işletme | El katmanı protokolü tamamen asistan disiplinine bağlıydı: 51. oturum kaydı **kayıp**, 6 Eyl 15:25 kaydı yok; mekanik… | SurecIsletmeScars.Y085_CheckpointMustBeCompleteAndVerified | yeşil |
| Y-086 | süreç-işletme | CHANGELOG 0.5.0 "exe" iddiası yanlıştı (Inno kurulu değildi); "8 bayat 'not built' iddiası" sürüm öncesi vitrin… | SurecIsletmeScars.Y086_ReleaseClaimsRequireEvidence | yeşil |
| Y-087 | süreç-işletme | Bekleyici döngüde `2>/dev/null` hatayı yuttu → döngü sonsuza dek yokladı; ayrıca yol hedef yorumlayıcının diliyle… | SurecIsletmeScars.Y087_WaitLoopIsBoundedAndSurfacesFailure | yeşil |
| Y-088 | süreç-işletme | Mekanizma "çalışıyor" görünürken de kayıp veriyordu: bir tarama yardımcı oturumu ana oturum sandı, yerel model bir… | SurecIsletmeScars.Y088_DoctorHealthRequiresCoverageAndRejectionTargets | yeşil |
| Y-089 | yazma-yolu | Hook stdin'i BOM'lu yazılınca strict JSON reddedildi | YazmaYoluScars.Y089_BomInputIsAcceptedButOutputHasNoBom | yeşil |
| Y-090 | kanca | Hook hem user hem project settings'te kayıtlıysa iki kez ateşler, iki daily girişi oluşur | KancaScars.Y090_DuplicateHookRegistrationAndFlushAreRejected | yeşil |
| Y-091 | yazma-yolu | 60 saniyelik dedupe aynı transkripti 14 dk sonra ikinci kez özetledi | YazmaYoluScars.Y091_NoNewTurnsSkipsModelCall | yeşil |
| Y-092 | yazma-yolu | Tur sayısı karakter tavanından önce sayıldı; kısa oturum "boş" sanıldı | YazmaYoluScars.Y092_MinTurnsUsesPostCapTurnCount | yeşil |
| Y-093 | derleyici | Akşam saati tek kapıysa gündüz makinesi hiç derlemez | DerleyiciScars.Y093_MaybeCompileUsesTwentyHourRule | yeşil |
| Y-095 | derleyici | Satır başı directive gate'i U+2028 / iç BOM ile atlatılır | DerleyiciScars.Y095_UnicodeNormalizationPrecedesDirectiveDetection | yeşil |
| Y-096 | derleyici | `tags: [a, b` toleranslı parser'da boş liste sanıldı | DerleyiciScars.Y096_MalformedTagsAreRejected | yeşil |
| Y-101 | yazma-yolu | `save "<metin>"` "kayıt yazıldı" deyip rc 0 döndü ama hiçbir şey yazmadı: doğrulayıcı baytları yalnız bellekte tur attırıyordu | YazmaYoluScars.Y101_CheckpointIsWrittenToDailyAndVerified | yeşil |
| Y-102 | yazma-yolu | `--vault X save "metin"` çağrısında kayıt metni `args[1]`'den okundu, yani kasa yolundan; her koşum "eksik alan: karar, düzeltme, devir" dedi | YazmaYoluScars.Y102_SaveTextIsTheFirstPositionalAfterTheCommand | yeşil |
| Y-107 | yazma-yolu | `save --session-json` içindeki `turns[0].text` nesne olduğunda yakalanmayan `FormatException` ile süreç öldü (rc -532462766) | YazmaYoluScars.Y107_SaveSessionJsonRejectsMalformedInputWithoutEscapingProgram | yeşil |
| Y-109 | süreç-işletme | `Main`'in son çare istisnası yakalanmıyordu — yakalanmayan hata Windows Hata Bildirimi ("oom.exe - Uygulama Hatası") kutusunu açıyor, hook'u askıda bırakıyordu | SurecIsletmeScars.Y109_MainCatchesUnhandledExceptionAndDisablesWerDialog | yeşil |
| Y-113 | kanca | `Runner.RunProcess`, tek bir `claude -p` görev denemesinin (ör. `--max-turns 1` aşımı) çıkış 1'ini, gerçek erişilebilirlikle karışan kalıcı `hooks/hook-failed` doctor hatasına dönüştürüyordu; sonraki başarılar bunu hiç iyileştirmiyordu | KancaScars.Y113_ReachabilityProbeIsIndependentOfTaskFailuresAndHealsOnSuccess | yeşil |
| Y-125 | test-disiplini | Test paketi Y-119…Y-123'ü DisplayName'e ekledi ama `docs/scars.md` hiç güncellenmedi — defter test paketinin gerisinde sessizce kalabiliyordu; artık her `[Fact]`/`[Theory]` DisplayName'indeki her Y-numarası `scars.md`'de bir satıra bağlı olmak zorunda | TestDisipliniScars.Y125_EveryDisplayedScarNumberHasAScarsMdRow | yeşil |
| Y-132 | durum-deposu | `State` kurucusunda `Directory.CreateDirectory` çağırıyordu ve salt-okunur komutlar bile durum açıyordu: her `--vault <geçici>` koşumu bir kök bırakıyordu — makinede 26 başıboş kök ölçüldü. `StateAccess.ReadOnly` hiçbir dizin yaratmıyor; sağlık defteri okuması da artık kök yaratmıyor | DurumDeposuScars.Y132_* | yeşil |
| Y-140 | getirme | `notes_fts` ham not metninden kuruluyordu (FTS5 `unicode61` okur) ama sorgular `TurkishFold`'dan geliyordu: fikstürde altı Türkçe probun **üçü** sıralayıcıda doğru notu bulurken FTS'ten **boş** dönüyordu (`kapısı`, `çalışıyor`, `güvenlikten`). Ölü kodu olduğu gibi bağlamak o notları sessizce düşürürdü. İndeks artık fold edilmiş jetonlardan kuruluyor ve manifest digest'i indeks **biçimini** de kapsıyor | IndeksScars.Y140_* | yeşil |
| Y-141 | getirme | `Retrieve.Candidates` FTS sorgusunu ve bm25 ağırlıklarını taşıyordu ama **hiçbir yerden çağrılmıyordu** — testteki aynı adlı yardımcı kendi ham SQL'ini koşuyordu, yani kapsama gibi görünen şey paralel bir kopyayı sınıyordu. `Query` ve `Hook` artık tek `Search()` üzerinden indekse gidiyor; `CandidateSource` (`fts` / `corpus-scan:no-index` / `corpus-scan:stale-index`) sayesinde bir koşum tarama yaparken "bağlandı" diyemiyor | GetirmeScars.Y141_* | yeşil |
| Y-161 | durum-deposu | Salt-okunur komutlar durum kökü yaratıyordu: `VaultPaths.StateDatabase()` sadece yolu sorana bile `Directory.CreateDirectory` çağırıyor, `OpenState()` ise koşulsuz yazma modunda açıyordu — her `--vault <geçici>` koşumu bir kök bırakıyordu. Kurulu exe ile kanıtlandı: `context`/`retrieve`/`mcp`/`doctor` artık kök yaratmıyor, `compile --dry-run` yaratıyor | DurumDeposuScars.Y161_* | yeşil |
| Y-171 | durum-deposu | `Retrieve.Build()` kendi `Directory.CreateDirectory` çağrısını yapıyordu: test paketi her koşumda bir durum kökü sızdırıyordu (ölçüldü, sızan kökte yalnız indeks tabloları ve `user_version=0` vardı). Artık var olan dizin yazmanın koşulu; sızıntı bitti | IndeksScars.Y171_* | yeşil |
| Y-172 | durum-deposu | Yeniden kurulum `DROP TABLE notes` ile şema sahibinin altından tabloyu alıyordu. Yerine aynı işlem içinde `DELETE` geldi: indeks indeks olarak yenileniyor, `ix_notes_updated` ve sürüm damgası yerinde kalıyor | IndeksScars.Y172_* | yeşil |
| Y-176 | durum-deposu | `Retrieve.Build()` işini denetlemeden başarı bildiriyordu — özellikle yeniden kurmayı atladığı yolda. `IndexVerifier` eksik/fazla/içerik kayması/digest uyuşmazlığı arıyor ve başarısızlıkta çıkış 1 veriyor; `Doctor` ikinci bir görüş üretmiyor, aynı doğrulayıcıya soruyor | IndeksScars.Y176_* | yeşil |
| Y-300 | kanca | UserPromptSubmit kancası her promptta hafıza enjekte edip etmeyeceğini tahmin ediyordu; artık sayıyor. On beşinci mesajda companion katmanını hatırlatır, aradaki on dörtte susar | SadeScars.Y300_NudgeSpeaksOnEveryFifteenthPromptAndNotBetween | yeşil |
| Y-301 | kanca | Last-Session.md'ye dokunulmadan kapanan uzun oturum hiçbir iz bırakmıyordu: sessionend flush'ı bir `hafiza/yansima-borcu` satırı yazar, bir sonraki `context` onu [Bildirim]'in başında basar ve satırı düşürür | SadeScars.Y301_UntouchedCompanionAfterALongSessionLeavesExactlyOneReflectionDebtRow | yeşil |
| Y-302 | durum-deposu | Şema merdiveni, `user_version` damgası, yedek-doğrula-göç ve görünümler gitti: state.db silinebilir bir önbellektir, sonraki açılış on tabloyu tek bir `CREATE TABLE IF NOT EXISTS` kümesiyle sıfırdan kurar | SadeScars.Y302_DeletedStateFileIsRebuiltFromScratchOnTheNextOpen | yeşil |
| Y-303 | kurulum | Kurulum tek kapsama indi: yalnız `<vault>\.oom\` dosyaları ve proje `settings.json` içindeki dört kanca. Kancalar birleştirilerek yazılır, `--uninstall` yalnız o dördünü düşürür ve vault dışında hiçbir yol açılmaz | SadeScars.Y303_ProjectScopeInstallTouchesNothingOutsideTheVault | yeşil |
| Y-304 | durum-deposu | Eski şemalı `state.db` (merdiven damgası ya da `prompt_count`suz `sessions`) göç edilmez: `state.db.eski-<ts>` diye kenara alınır, silinmez, yenisi sıfırdan kurulur ve `health`'e `eski-sema` satırı düşer. |

## Sınıf başına durum

| Sınıf | Yeşil | Kırmızı |
|---|---|---|
| yazma-yolu | 20 | 0 |
| özetleyici | 3 | 0 |
| derleyici | 15 | 0 |
| getirme | 6 | 0 |
| kanca | 11 | 0 |
| durum-deposu | 12 | 0 |
| kurulum | 1 | 0 |
| test-disiplini | 7 | 0 |
| süreç-işletme | 5 | 0 |
| **Toplam** | **80** | **0** |
