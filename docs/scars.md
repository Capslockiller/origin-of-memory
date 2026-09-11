<!-- yazan: codex · gpt-6 -->
# Yara Durumu — per-scar test tablosu

Ölçüm: `dotnet test Oom.sln -c Release` — Toplam yara sayısı: 125, Geçti: 125, Başarısız: 0. Kapı testleriyle birlikte toplam 166 test, 166 geçti. Tarih: 2026-09-11, şerit SOZ, `9b116e0` üstü.

Fixture kurmadan davranış iddia ettikleri için kırmızı bırakılan 7 yara (Y-035, Y-039, Y-042, Y-046, Y-050, Y-069, Y-098) şerit CI tarafından kapatıldı: eksik davranış yazıldı, fixture'lar depoya girdi. Gerçekler `progress.md` içinde `## Lane CI` bölümündedir.

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
| Y-009 | yazma-yolu | Zamanlanmış görev durabilecek bir tekrar kaydetti (`StopAtDurationEnd = True` + boş süre); ayrıca 7 Eylül 10:00 koşusu… | YazmaYoluScars.Y009_ScheduledTaskOmitsDurationAndDoctorAcceptsWakeRun | yeşil |
| Y-010 | yazma-yolu | Task-notification gövdeleri ve araç anlatıları kullanıcı metni gibi flush'a girdi; 8 Eyl daily'sinin 16:44 bloğu makine… | YazmaYoluScars.Y010_MachineEnvelopeIsNotSummarized | yeşil |
| Y-011 | yazma-yolu | `claude -p` kullanıcının plan modunu miras aldı ve `<ExitPlanMode/>` ile sohbet etti: **110 flush'ın 23'ü (%21)… | YazmaYoluScars.Y011_ClaudeRunnerIsIsolatedAndFallsBack | yeşil |
| Y-012 | yazma-yolu | Reddedilen özet kayboluyordu ve kaynağını tüketiyordu: reddedilen/boş derleyici çıktısı geldiği daily'yi "işlendi" saydı | YazmaYoluScars.Y012_RejectedSummaryDoesNotAdvanceCursor | yeşil |
| Y-013 | yazma-yolu | 13 `missing-transcript`: kanca payload'ındaki yol taramada bulunamadı | YazmaYoluScars.Y013_MissingTranscriptIsRelocatedBySessionId | yeşil |
| Y-014 | yazma-yolu | Canlı vault'ta flush'ta günlük-dosya kilidi yoktu (repo kopyasında vardı) → eşzamanlı yazmada blok kaybı | YazmaYoluScars.Y014_ConcurrentDailyAppendsLoseNoBlock | yeşil |
| Y-015 | yazma-yolu | Yerleşme penceresi kontrolü (`current - mtime < fresh_seconds`) pencere `0` (kapalı) iken bile saatin önünde `mtime`… | YazmaYoluScars.Y015_ZeroFreshWindowDisablesFiltering | yeşil |
| Y-016 | özetleyici | Yerel qwen3:8b üretim penceresinde **%14,5 uydurma** üretti (30B %8,8 · 8B sıkı sözleşme %8,8 · Haiku %5,9); 17,6k… | OzetleyiciScars.Y016_UnmeasuredFallbackCannotEnterNormalConfidence | yeşil |
| Y-017 | özetleyici | Özet doğrulayıcı sorunsuz özetleri reddetti: 8 Eyl taramasında 20 Haiku özetinin 6'sı `summary-schema-invalid` diye… | OzetleyiciScars.Y017_SummaryValidatorNormalizesNoiseAndPersistsRejection | yeşil |
| Y-018 | özetleyici | Sabit model takma adları kullanılıyordu (`haiku` takma adı Sonnet'e çözümleniyordu) — çağrılan modelin kim olduğu… | OzetleyiciScars.Y018_ModelIdMustBeExactAndRecordedFromResponse | yeşil |
| Y-019 | özetleyici | Ollama'nın düşünme modu varsayılan açıktı ve yanıt gelmeden bütün jeton bütçesini tüketiyordu | OzetleyiciScars.Y019_LocalRequestDisablesThinkingAndCapsGeneration | yeşil |
| Y-020 | özetleyici | Yerel backend'de koşan flush, backend seçimini tetiklediği derleyiciye sızdırıyordu | OzetleyiciScars.Y020_ComponentBackendSelectionIsSealed | yeşil |
| Y-021 | özetleyici | Çağrı defteri Ollama model yükleme süresini çıkarım süresinden ayıramıyor: flush medyan 14,0 sn · p95 60,4 sn ·… | OzetleyiciScars.Y021_LocalTimingSeparatesLoadPromptAndGeneration | yeşil |
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
| Y-033 | derleyici | Çok makineli çift derleme mümkündü (çapraz makine kilidi kooperatif) | DerleyiciScars.Y033_CrossMachineCompileLockAllowsSinglePublisher | yeşil |
| Y-034 | derleyici | `warn:registry-truncated` başarılı bir seçim telemetrisi olduğu hâlde uyarı sayılıyordu ve gerçek uyarıları gürültüde… | DerleyiciScars.Y034_TelemetryDoesNotHidePendingDailies | yeşil |
| Y-035 | getirme | El katmanındaki güncel düzeltmeler indeks nüfusunun dışındaydı (`retrieve.py:612-614` yalnız `knowledge/concepts`)… | GetirmeScars.Y035_CorrectionLayerOutranksStaleConcept | yeşil |
| Y-036 | getirme | İlgililik kapısı iki jeton örtüşmesiyle geçiyordu: genel kelimeler ve dosya-yolu parçaları semantik kanıt sayıldı… | GetirmeScars.Y036_JunkAndPathPromptsInjectNothing | yeşil |
| Y-037 | getirme | Hafıza kancası iç `claude -p` çağrılarına kişisel not enjekte etti: `BEYIN_INVOKED_BY` özyineleme bekçisi yalnız emekli… | GetirmeScars.Y037_RecursiveInvocationIsSilent | yeşil |
| Y-038 | getirme | Getirmenin **iki ayrı kapısı** vardı: emekli `memory-retrieve.ps1` kendi atlama mantığını ve ilgililik kapısı olmayan… | GetirmeScars.Y038_RetrieveEntryPointsAreIdentical | yeşil |
| Y-039 | getirme | Oturum içi tekrar-engelleme notu **oturum boyu** gizliyordu: defter yalnız not adına anahtarlanmıştı, sonraki farklı… | GetirmeScars.Y039_DedupeIncludesQuerySignature | yeşil |
| Y-040 | getirme | BM25 alan ağırlıkları hiç uygulanmıyordu: `name UNINDEXED` sütunu ağırlıkların ilkini (8'i) yutuyordu, fiili ağırlıklar… | GetirmeScars.Y040_Bm25WeightsIncludeUnindexedLeadingZero | yeşil |
| Y-041 | getirme | Tazelik çarpanı ölçek-körü biçimde füzyon skorunu domine ediyordu (%1,6'lık bant 4× ile çarpılıyordu) ve güçlü… | GetirmeScars.Y041_UnknownRetrievalModeFails | yeşil |
| Y-042 | getirme | Faz 1 el katmanını indekse ekleyince **gold set geriledi**: recall@3 102→94, recall@5 110→103 (zemin 110); uzun el… | GetirmeScars.Y042_RetrievalChangeMustPassBothRecallAxes | yeşil |
| Y-043 | getirme | Junk kapısı "dur bana soru sorma, hafıza kaybı yaşıyorsun" cümlesine 59. oturumun "HAFIZA KAYBI TEŞHİSİ" notunu bastı… | GetirmeScars.Y043_IntentGateOverridesTopicOverlap | yeşil |
| Y-044 | getirme | Türkçe katlama `ToLowerInvariant` ile yanlış: `I→ı`, `İ→i` dönüşümü özel tablo ister; ham `unicode61` tokenizer'da İ/ı… | GetirmeScars.Y044_TurkishFoldHandlesDottedAndDotlessI | yeşil |
| Y-045 | kanca | Açılış bağlamı çoğunlukla sabit ~4,2–4,8k jetonluk vergi: 87 başlangıcın ortalaması **15.134 karakter**, %70,66'sı… | KancaScars.Y045_ContextKeepsCurrentDataWithinCap | yeşil |
| Y-046 | kanca | SessionStart kancası aynı saniyede iki kez ateşledi (87 enjeksiyon kaydının 16'sı çift `ts`): Desktop'ın açtığı… | KancaScars.Y046_HelpersAreDedupedBySessionAndEventIdentity | yeşil |
| Y-047 | kanca | Uzun Windows oturumlarında kancalar ~2,5 saniye sonra sessizce duruyor (üst-akım #16047); resmî doküman SessionEnd'i… | KancaScars.Y047_SystemWorksWhenAllHooksAreSilent | yeşil |
| Y-048 | kanca | Kanca sayaçları oturumlar arasında paylaşımlıydı (`prompt_count` / `session_start_time` tek dosyada) | KancaScars.Y048_HookCountersArePerSession | yeşil |
| Y-049 | kanca | Python yoksa kancalar sessizce "başarılı" oluyordu | KancaScars.Y049_HookFailureIsPersisted | yeşil |
| Y-050 | kanca | El katmanının tek mekanik denetimi (`session-end.ps1` içindeki `needs_reflection`) SessionEnd'e bağlıydı — yani tam da… | KancaScars.Y050_CompanionAuditUsesContentNotMtime | yeşil |
| Y-051 | kanca | `CLAUDE.md`'nin enjeksiyon listesi bayattı (Kurallar ve Journal eksikti); Kurallar (2.866 B) ve Journal (406 B) her… | KancaScars.Y051_ContextSectionsMatchDocumentedList | yeşil |
| Y-052 | kota | Kota okuyucu önbelleklenmiş/bayat yüzdeyi canlı gibi gösterdi: 22:19'daki tablo **Codex 5s %64** derken 22:46'daki… | KotaScars.Y052_LiveQuotaOverridesSilentEventLog | yeşil |
| Y-053 | kota | Canlı okumadan önce zincir sırayla 6 saat toleranslı statusline önbelleğine ve 300 sn'lik OAuth önbelleğine bakıyordu… | KotaScars.Y053_FailedLiveReadPrintsNoPercentage | yeşil |
| Y-054 | kota | Bayat kota kaynağı "serbest" okunuyordu: OAuth önbelleği 36 saattir donmuşken her okuma **aynı yüzdeleri taze zaman… | KotaScars.Y054_FrozenObservationIsUnknownNotFree | yeşil |
| Y-055 | kota | OAuth erişim jetonu 5 Eylül 14:04 UTC'de doldu, uç `401 OAuth access token has expired` dönüyor; okuyucunun yenileme… | KotaScars.Y055_ExpiredOAuthProvidesLoginResolution | yeşil |
| Y-056 | kota | Kota ucunun sözleşmesi belgesiz (`account/rateLimits/read` ve OAuth `usage` ucu resmî belgeli değil); alan adları her… | KotaScars.Y056_QuotaSchemaDriftIsVisible | yeşil |
| Y-057 | kota | Sıfırlama kredisi (`availableCount`) yalnız okundu; `consume` metodunun adı bile doğrulanmadı — durum değiştiren bir… | KotaScars.Y057_QuotaReaderNeverConsumesCredit | yeşil |
| Y-058 | durum-deposu | Harcama defteri her yanıtı iki-üç kez saydı: gerçek arşivde (1.740 dosya) **50.596 ham `usage` satırı 22.036 farklı… | DurumDeposuScars.Y058_UsageIsDeduplicatedByMessageId | yeşil |
| Y-059 | durum-deposu | Harcama defteri Master oturumlarıyla alt ajanları ayırmıyor: 6 Eylül Claude cache-read'inin **50,8M'i, yani %31,8'i**… | DurumDeposuScars.Y059_OwnerClassesSumToDeduplicatedTotal | yeşil |
| Y-060 | durum-deposu | Günlük toplamlar oturumun **son** zaman damgasına anahtarlanıyordu: gece yarısını aşan bir oturumun tüm harcaması… | DurumDeposuScars.Y060_UsageIsAssignedByResponseTimestamp | yeşil |
| Y-061 | durum-deposu | Durum dosyaları sınırsız büyüdü: `harcama-defteri.json` **4,5 MB**'a şişti; `enjeksiyon.jsonl`, getirme defterleri ve… | DurumDeposuScars.Y061_StateRetentionBoundsOneYearDatabase | yeşil |
| Y-062 | durum-deposu | **977** `retrieve-session-*.json` dosyası birikti; 7 günlük budama hiç çalışmadı | DurumDeposuScars.Y062_RetrieveRowsOlderThanSevenDaysArePruned | yeşil |
| Y-063 | durum-deposu | `health`/ledger yazımları kilitsizdi ve `health` eski uyarıları temizlemiyordu | DurumDeposuScars.Y063_HealthWritesAreLockedAndWarningsAge | yeşil |
| Y-064 | durum-deposu | `_atomic_write_json` geçici dosya adı yalnız pid taşıyordu → tek süreç içindeki şerit iş parçacıkları aynı geçici… | DurumDeposuScars.Y064_AtomicWriteUsesUniqueTemporaryNamesAndRetries | yeşil |
| Y-065 | kurulum | Windows'ta `chmod 0600/0700` etkisiz ve DACL hiç kurulmuyordu — durum ve ham veri dizinleri diğer kullanıcılara açıktı | KurulumScars.Y065_SensitiveDirectoriesReceiveUserOnlyAcl | yeşil |
| Y-066 | kurulum | Dağıtım = script kopyasıydı: vault ile repo arasında 7 py + 2 kanca ayrışmıştı ve düzeltmeler karşılıklı eksik… | KurulumScars.Y066_InstalledBinaryDriftIsReported | yeşil |
| Y-067 | kurulum | 58 adet `BEYIN_*` ortam değişkeni vardı, merkezi şema yoktu; `BEYIN_FAKE_*` üretimde açıktı; eski kullanıcı env… | KurulumScars.Y067_NoHardCodedUserPathsAndUnknownConfigurationFails | yeşil |
| Y-068 | kurulum | Tanrı modüller: `compile` 1.771, `kule` 1.701, `retrieve` 1.283, `nezaket` 1.197 satır; ayrıca frontmatter… | KurulumScars.Y068_ModuleLineBudgetsAndSingleParserAreEnforced | yeşil |
| Y-069 | kurulum | **"Derlendi ≠ çalışıyor":** Inno'da üç hata (`{userprofile}` sabiti yok · `dontcopy` DestDir'i yok sayıyor ·… | KurulumScars.Y069_WindowsVmEndToEndChainWorks | yeşil |
| Y-070 | kurulum | `kur.ps1`, 504 karakterlik JSON donanım sondasını native argüman olarak geçiriyordu; Windows PowerShell JSON içindeki… | KurulumScars.Y070_StructuredDataCrossesProcessBoundaryViaStdinOrFile | yeşil |
| Y-071 | kurulum | `setx` ile kalıcılaştırma değişkenleri kırpıyordu; Claude Desktop MCP kaydı MSIX-sanallaştırılmış yolu görmüyordu | KurulumScars.Y071_EnvironmentPersistenceAndMsixDiscoveryAreLossless | yeşil |
| Y-072 | kurulum | İlk gerçek Windows koşumunda (Linux'ta yazılmış süit) üç Windows'a özgü kusur çıktı: `kule.py` cwd bekçisi… | KurulumScars.Y072_WindowsSpecificOutcomesAreInterpreted | yeşil |
| Y-073 | kurulum | PATH'teki `codex` bir npm sarmalayıcısı (`codex.cmd`); `subprocess.Popen(["codex", ...])` **WinError 2/193** veriyor | KurulumScars.Y073_CommandWrapperResolvesToRunnableEntryPoint | yeşil |
| Y-074 | kurulum | Kurtarma prosedürü yoktu ve en son yedek 2 Eylül'dendi | KurulumScars.Y074_MigrationStopsBeforeWritesWhenBackupFails | yeşil |
| Y-075 | test-disiplini | Kalite kapıları gerçek doğruluğu ölçmüyordu: `containment` örneklemi kaynak metinde bulunan **yanlış** iddiayı… | TestDisipliniScars.Y075_AccuracyAxesAreReportedSeparately | yeşil |
| Y-076 | test-disiplini | Testlerin hiç görmediği yüzeyler: olgu hatırlama, kapsama, maliyet doğruluğu, reddedilen çıktıdan kurtarma, çok dosyalı… | TestDisipliniScars.Y076_IngestParsersUseFixedExternalContractSamples | yeşil |
| Y-077 | test-disiplini | Bir test iki PowerShell kanca başlangıcının **aynı duvar-saati saniyesine** düşmesine dayanıyordu ve CI'da düştü | TestDisipliniScars.Y077_TimingTestUsesInjectedClock | yeşil |
| Y-078 | test-disiplini | Tarih-bombalı 2 CI testi vardı; `durum`'un çağrı özeti gerçek saat dönümünde kırılıyordu | TestDisipliniScars.Y078_DateBoundaryUsesFakeNow | yeşil |
| Y-079 | test-disiplini | `pytest ... \ | TestDisipliniScars.Y079_CiPreservesTestExitCodeWithoutPipelineMasking | yeşil |
| Y-080 | test-disiplini | CI'ın temp-altı ret testi, `%TEMP%`'i 8.3 kısa yol (`RUNNERADMIN~1`) olan runner'larda düştü: sahte temp kökü hiç… | TestDisipliniScars.Y080_ShortAndLongWindowsPathsNormalizeToSameRoot | yeşil |
| Y-081 | test-disiplini | Çağrı yeri amaç taraması çalışan bir checkout'ta düştü (depo geneli tarama, kendi test verisini de tarıyordu) | TestDisipliniScars.Y081_StaticScanExcludesFixturesOnly | yeşil |
| Y-082 | test-disiplini | Kabul ölçümü tek eksenliydi: Faz 1'in epizodik kazancı gold set'i düşürdü ve bu ancak kabul koşumunda görüldü (bkz. Y… | TestDisipliniScars.Y082_AcceptanceGateRequiresPrimaryAndCounterMetric | yeşil |
| Y-083 | süreç-işletme | `git worktree remove --force` döngüsü ilgisiz bir worktree kaydını (`origin-of-memory-panel`, dal `arayuz/panel`) da… | SurecIsletmeScars.Y083_CleanupNeverRemovesUnnamedWorktree | yeşil |
| Y-084 | süreç-işletme | Backend değişikliği (yerel → Haiku) yan etki envanteri çıkarılmadan yapıldı → Y-006'daki kendini besleyen döngü | SurecIsletmeScars.Y084_NewRunnerRequiresTranscriptExclusion | yeşil |
| Y-085 | süreç-işletme | El katmanı protokolü tamamen asistan disiplinine bağlıydı: 51. oturum kaydı **kayıp**, 6 Eyl 15:25 kaydı yok; mekanik… | SurecIsletmeScars.Y085_CheckpointMustBeCompleteAndVerified | yeşil |
| Y-086 | süreç-işletme | CHANGELOG 0.5.0 "exe" iddiası yanlıştı (Inno kurulu değildi); "8 bayat 'not built' iddiası" sürüm öncesi vitrin… | SurecIsletmeScars.Y086_ReleaseClaimsRequireEvidence | yeşil |
| Y-087 | süreç-işletme | Bekleyici döngüde `2>/dev/null` hatayı yuttu → döngü sonsuza dek yokladı; ayrıca yol hedef yorumlayıcının diliyle… | SurecIsletmeScars.Y087_WaitLoopIsBoundedAndSurfacesFailure | yeşil |
| Y-088 | süreç-işletme | Mekanizma "çalışıyor" görünürken de kayıp veriyordu: bir tarama yardımcı oturumu ana oturum sandı, yerel model bir… | SurecIsletmeScars.Y088_DoctorHealthRequiresCoverageAndRejectionTargets | yeşil |
| Y-089 | yazma-yolu | Hook stdin'i BOM'lu yazılınca strict JSON reddedildi | YazmaYoluScars.Y089_BomInputIsAcceptedButOutputHasNoBom | yeşil |
| Y-090 | kanca | Hook hem user hem project settings'te kayıtlıysa iki kez ateşler, iki daily girişi oluşur | KancaScars.Y090_DuplicateHookRegistrationAndFlushAreRejected | yeşil |
| Y-091 | yazma-yolu | 60 saniyelik dedupe aynı transkripti 14 dk sonra ikinci kez özetledi | YazmaYoluScars.Y091_NoNewTurnsSkipsModelCall | yeşil |
| Y-092 | yazma-yolu | Tur sayısı karakter tavanından önce sayıldı; kısa oturum "boş" sanıldı | YazmaYoluScars.Y092_MinTurnsUsesPostCapTurnCount | yeşil |
| Y-093 | derleyici | Akşam saati tek kapıysa gündüz makinesi hiç derlemez | DerleyiciScars.Y093_MaybeCompileUsesTwentyHourRule | yeşil |
| Y-094 | kurulum | PS 5.1 BOM'suz dosyayı ANSI okur, Türkçe harf bozulur | KurulumScars.Y094_RepositoryHasNoPowerShellAndTextIsUtf8WithoutBom | yeşil |
| Y-095 | derleyici | Satır başı directive gate'i U+2028 / iç BOM ile atlatılır | DerleyiciScars.Y095_UnicodeNormalizationPrecedesDirectiveDetection | yeşil |
| Y-096 | derleyici | `tags: [a, b` toleranslı parser'da boş liste sanıldı | DerleyiciScars.Y096_MalformedTagsAreRejected | yeşil |
| Y-097 | kurulum | ctypes `HANDLE` 32-bit'e kırpıldı | KurulumScars.Y097_HandlePreservesSixtyFourBitValue | yeşil |
| Y-098 | kurulum | `agy` stdin kapatılmayınca asılı kaldı | KurulumScars.Y098_RunnerClosesStdinAndHonorsTimeout | yeşil |
| Y-099 | süreç-işletme | `git add -A` `settings.local.json.yedek`'i ekledi | SurecIsletmeScars.Y099_BackupsStayUnderOomBackupAndAreIgnored | yeşil |
| Y-100 | kurulum | Tek dosya yayını yerel kütüphaneyi dışarıda bıraktı; kurulan kopya `e_sqlite3.dll` olmadan `DllNotFoundException` ile öldü (rc -532462766) | KurulumScars.Y100_SingleFilePublishEmbedsNativeLibraries | yeşil |
| Y-101 | yazma-yolu | `save "<metin>"` "kayıt yazıldı" deyip rc 0 döndü ama hiçbir şey yazmadı: doğrulayıcı baytları yalnız bellekte tur attırıyordu | YazmaYoluScars.Y101_CheckpointIsWrittenToDailyAndVerified | yeşil |
| Y-102 | yazma-yolu | `--vault X save "metin"` çağrısında kayıt metni `args[1]`'den okundu, yani kasa yolundan; her koşum "eksik alan: karar, düzeltme, devir" dedi | YazmaYoluScars.Y102_SaveTextIsTheFirstPositionalAfterTheCommand | yeşil |
| Y-103 | kurulum | Flush `claude`'u çıplak adla başlattı; npm yalnız `claude.cmd` bıraktığı makinede her oturum Retry'a düştü — çözümleyici (Y-073) bu yolda hiç çağrılmıyordu | KurulumScars.Y103_ClaudeRequestUsesResolvedExecutable | yeşil |
| Y-104 | kurulum | Kurulumun yazdığı varsayılan `oom.json` kod varsayılanlarının elle yazılmış ikinci bir kopyasıydı: `sweep.roots: []` ile temiz kurulum hiçbir şey taramadı | KurulumScars.Y104_InstallerConfigurationComesFromCodeDefaults | yeşil |
| Y-105 | kurulum | Kurulu kopyadan `--uninstall` kendi exe'sini silmeye çalışıp çöktü; kancalar yerinde kaldı | KurulumScars.Y105_UninstallFromInstalledCopySurvivesSelfDelete | yeşil |
| Y-106 | yazma-yolu | `--vault X ingest codex` çağrısında kaynak `args[1]`'den okunduğu için kasa yolu kaynak sanıldı | YazmaYoluScars.Y106_IngestSourceIsTheFirstPositionalAfterTheCommand | yeşil |
| Y-107 | yazma-yolu | `save --session-json` içindeki `turns[0].text` nesne olduğunda yakalanmayan `FormatException` ile süreç öldü (rc -532462766) | YazmaYoluScars.Y107_SaveSessionJsonRejectsMalformedInputWithoutEscapingProgram | yeşil |
| Y-108 | kurulum | Başarılı `install --uninstall` kaldırma yerine `kurulum tamam`, başarısızlık da `kurulum başarısız` yazdı | KurulumScars.Y108_UninstallReportsUninstallOutcome | yeşil |
| Y-109 | süreç-işletme | `Main`'in son çare istisnası yakalanmıyordu — yakalanmayan hata Windows Hata Bildirimi ("oom.exe - Uygulama Hatası") kutusunu açıyor, hook'u askıda bırakıyordu | SurecIsletmeScars.Y109_MainCatchesUnhandledExceptionAndDisablesWerDialog | yeşil |
| Y-110 | getirme | Küçük konu derleminde yaygın terimlerin IDF tabanı, doğru ilk notun ortalama skorunu sabit kapının altında bıraktı; 19 notta üç kısa soru boş döndü | GetirmeScars.Y110_SmallCorpusGateKeepsRelevantTopHitAndRejectsUnrelatedPrompt | yeşil |
| Y-111 | kurulum | `install --from-v0`, `.claude/scripts` ve `.claude/hooks` ağaçlarını bütünüyle taşıyıp kota ve status-line gibi v0 dışı araçları sessizce kırıyordu | KurulumScars.Y111_FromV0MigrationMovesOnlyOwnedFilesAndKeepsForeignTools | yeşil |
| Y-112 | yazma-yolu | `ingest`, `Program.cs`'te her zaman boş dosya listesiyle çağrılıyordu (`new Ingest().Run(source, [], ...)`); `sweep.roots` altında hiçbir dosya keşfi yoktu, her koşum "içe aktarım: 0 oturum" yazıyordu | YazmaYoluScars.Y112_IngestDiscoversTranscriptsUnderConfiguredRoots | yeşil |
| Y-113 | kanca | `Runner.RunProcess`, tek bir `claude -p` görev denemesinin (ör. `--max-turns 1` aşımı) çıkış 1'ini, gerçek erişilebilirlikle karışan kalıcı `hooks/hook-failed` doctor hatasına dönüştürüyordu; sonraki başarılar bunu hiç iyileştirmiyordu | KancaScars.Y113_ReachabilityProbeIsIndependentOfTaskFailuresAndHealsOnSuccess | yeşil |
| Y-114 | durum-deposu | `Ingest`'in digest-dedupe durumu (`MemoryIngestStateStore`) süreç ömürlüydü: her yeni `oom ingest` çalıştırılışı aynı en eski dosyaları yeniden "içe aktarım" sayıyor, arşiv hiç ilerlemiyordu — canlı makinede dört ardışık `ingest claude --max 25` hep "atlanan 0" verdi | DurumDeposuScars.Y114_IngestCompletionPersistsAcrossStateReopen | yeşil |
| Y-115 | özetleyici | `Runner.CallLocal` bağlam penceresi göndermiyordu; `/chat/completions` gerçek Ollama'da `options.num_ctx`'i sessizce yok sayıyor — canlı kapı 10 koşumunda 5 daily'nin 4'ü HTTP 400 "context length", biri `llama-server`'ı çökertti (HTTP 500) | OzetleyiciScars.Y115_LocalCallCarriesContextWindowAndSettingWinsOverDefault | yeşil |
| Y-116 | test-disiplini | `Bench` 30 keşfedilen `.jsonl`'in 17'sini (alt-ajan izi + tur yok) modele hiç göndermeden ölçüme "başarısız" yazıyordu: ham 0,400/0,95 iken gerçek 13 transkript 0,923 ölçüyordu, n dürüst değildi | TestDisipliniScars.Y116_BenchExcludesSubagentAndNoTurnFilesButKeepsRealTranscript | yeşil |
| Y-117 | kurulum | Eski kasadan kopyalanan 182 daily `knowledge/concepts` içinde (554 not) zaten derlenmişti ama taze `state.db`'nin `daily_ingest`'i boştu; her `sweep` bunları yeniden derleme kuyruğuna alıyordu — canlı kasada `install --adopt` sonrası bekleyen 182'den 0'a düştü | KurulumScars.Y117_AdoptionMarksAlreadyCompiledDailiesWithoutRequeuing | yeşil |
| Y-118 | süreç-işletme | Kapsama sorgusu makinedeki HER transkripti (canlı kasada 2019 dosya, vault'tan aylar önce) sayıyordu; taze vault'ta "tüm zamanlar 18/2019" ve "son 7 gün %5,7" doctor'un tek `hata` satırıydı — doğru popülasyon vault'un kendi kurulum penceresi | SurecIsletmeScars.Y118_CoverageMeasuresTheVaultsOwnWindow | yeşil |
| Y-119 | durum-deposu | `Runner` ölçülen kullanımı `LastUsage` adlı, çağrılar arası paylaşılan tek bir alanda tutuyordu; alan yalnız başarılı bir Claude ayrıştırmasında güncelleniyor, başarısızlıkta sıfırlanmıyordu — ardından gelen bir çağrı yanıtı ayrıştıramayıp başarısız olsa bile ledger satırına önceki çağrının token sayıları yazılabiliyordu | DefterScars.Y119_LaterExhaustedCallDoesNotInheritEarlierUsage | yeşil |
| Y-120 | durum-deposu | Aynı paylaşılan `LastUsage` alanı zincirdeki hangi backend'in gerçekten yanıtladığından bağımsızdı: `Record` döngü bitince tek sefer çağrılıyor, `local` çağrısı ise hiç kullanım okumuyordu — claude→local fallback satırı gerçekte local'in ürettiği jetonlar yerine önceki bir çağrıdan kalma sayıları `claude` adına taşıyabiliyordu | DefterScars.Y120_FallbackUsageBelongsToTheBackendThatReportedIt | yeşil |
| Y-121 | durum-deposu | `calls` satırlarının kendi kimliği yoktu; aynı jeton sayılarına sahip iki ayrı deneme toplama görünümünde tek çağrıya indirgenebilirdi — her deneme artık kendi `operation_id`/`attempt_id`'siyle ayrı satır ve `v_call_usage`'ta ayrı `attempt_count`/`operation_count` olarak sayılıyor | DefterScars.Y121_IdenticalUsageDoesNotCollapseDistinctCalls | yeşil |
| Y-122 | durum-deposu | Eski `RecordCall` imzası jeton parametrelerini `0` varsayılanıyla alıyordu, "hiç ölçülmedi" ile "gerçekten sıfır jeton" ayırt edilemiyordu; artık ölçülmeyen kullanım NULL sütunlar ve `usage_source='unknown'`/`usage_rank=0` ile, gerçek sıfır ise `0` değerleri ve `usage_source='measured'`/`usage_rank=2` ile ayrı kaydediliyor | DefterScars.Y122_UnknownUsageIsDistinctFromMeasuredZero | yeşil |
| Y-123 | durum-deposu | Şema 3'ten göçte eski `in_tok` (cache dahil toplam girdi) yeni `uncached_in_tok` (yalnız cache-dışı girdi) sanılıp tahminle doldurulabilirdi; göç eski satırları OLDUĞU GİBİ bırakıyor — `uncached_in_tok`/`operation_id`/`attempt_id` NULL, `usage_rank=0`, `usage_semantics='legacy-total-input-v0'` — ve yalnız `user_version`'ı 4'e yükseltiyor | DefterScars.Y123_MigrationPreservesLegacyRowsWithoutGuessingUncachedInput | yeşil |
| Y-124 | test-disiplini | Geri döngü sözleşmesi (sayısal loopback zorunluluğu, yönlendirmenin izlenmemesi, proxy kullanılmaması, eski `localhost` yazımının DNS'siz çevirisi) yalnız kodda vardı, hiçbir belgede yazılı değildi | LoopbackScars.Y124_InstallDocStatesTheLoopbackContract | yeşil |
| Y-125 | test-disiplini | Test paketi Y-119…Y-123'ü DisplayName'e ekledi ama `docs/scars.md` hiç güncellenmedi — defter test paketinin gerisinde sessizce kalabiliyordu; artık her `[Fact]`/`[Theory]` DisplayName'indeki her Y-numarası `scars.md`'de bir satıra bağlı olmak zorunda | TestDisipliniScars.Y125_EveryDisplayedScarNumberHasAScarsMdRow | yeşil |

## Sınıf başına durum

| Sınıf | Yeşil | Kırmızı |
|---|---|---|
| yazma-yolu | 23 | 0 |
| özetleyici | 7 | 0 |
| derleyici | 16 | 0 |
| getirme | 11 | 0 |
| kanca | 9 | 0 |
| kota | 6 | 0 |
| durum-deposu | 13 | 0 |
| kurulum | 20 | 0 |
| test-disiplini | 11 | 0 |
| süreç-işletme | 9 | 0 |
| **Toplam** | **125** | **0** |
