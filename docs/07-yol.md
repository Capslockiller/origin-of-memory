# 07 — Yol

Sıra var, takvim yok. Her adım tek şerit, ≤300 satır (test ve fikstür hariç), bir ölçülen kapı. Kapı geçilmeden sonraki adım açılmaz. Sahibe her adımda diff + bir ölçüm + bir cümle gider; karar tablosu gitmez.

## Adım 0 — Tasarım yayını (bu depo)

- Kapı: `main` yalnız belge taşır; `v3` ve `v0` dalları arşiv; sahip README'yi okuyup "evet" der.

## Adım 1 — `kaydet`

- `surum/4.0` dalı `v3`ten açılır. Çekirdek kesilir: `Flush`, `Runner`, `Compile`, `Kit`, `Mcp`, `Doctor`, `Nudge`, `Save`, `Bridge`, `RootMap`, `DetachedProcess` silinir; `Ingest`, `Redactor`, `Guards`, `DailyFileLock`, `WriteAtomic`, `Configuration`, `Retrieve`, `Context` kalır. Derleme yeşil.
- `kaydet` yazılır: sınıflandırma, imleç türetme, blok yazımı, maske, idempotentlik. UserPromptSubmit/PreCompact/SessionEnd kancaları.
- **Kapı:** 7 gün gerçek kullanımda kapsama %100 (sahibin her oturumu için en az bir blok); aynı uuid iki kez yok; kanca süresi p95 <3 sn; 0 kayıtsız oturum. Ölçüm: `durum --uzun` ve `~/.claude/projects` ile çapraz sayım.
- Canlıya geçiş: `kur.ps1` 3.1.1'in yanına değil yerine kurar; `bin.onceki` geri alma kopyası.

## Adım 2 — `durum` ve `baglam`

- Tek satır sağlık; `baglam` yeni sırayla (durum · Kurallar · Düzeltmeler · kart dizini · son oturum). Henüz kart yoksa dizin boş.
- **Kapı:** `kaydet` durdurulunca `durum` 1 oturum içinde kırmızı üretir (gösterilir); `baglam` çıktısı ≤8.000 kr ve ≤8.500 bayt; ölçülen boyut Journal'da.

## Adım 3 — `kart`

- Şema, `dogrula`, `hesapla`, `goster`, `_dizin.md`; ilk mesajda eşleştirme; korur/değiştirir; çatışma.
- İlk kartlar elle: sahip hangi 5 konunun önce taşınacağını söyler; Threads.md'den kart çıkarmak budamadır.
- **Kapı:** 20 devam anında (sahip "odena <konu>" ile açar) kart yolu eski yoldan (Threads satırı) kötü değil; etiketi kör olarak Codex koyar; sahibe en çok 10 an gider. 6 haftada 12'den az an birikirse "yetersiz veri", kart enjeksiyonu açılmaz, kartlar elle kalır.
- Ek kapı: 3 bilinen sır satırı indekste, aramada ve bağlamda 0.

## Adım 4 — `ara`

- Korpus genişler (kart + Düzeltmeler + ham + arşiv); çekimserlik; `--kimlik`.
- **Kapı:** 20 sorguluk dondurulmuş set iki korpusta ≥ `rg` ve Obsidian tabanı; ilk indeks <30 sn; sorgu <1,5 sn. Geçemezse `ara` bırakılır, `rg` kalır; diğer fiiller etkilenmez.

## Adım 5 — Kes ve etiketle

- Kalan ölü yüzey silinir; testler davranış adıyla; `kur.ps1` tek kurucu; README son hâl; `4.0.0` etiketi, tek CHANGELOG girdisi.
- **Kapı:** `src` ≤3.000 satır, test <100 yöntem, tam kapı <60 sn, `oom --help` yalnız beş fiil.

## Kod tavanı

- Adım 1–4 toplam net eklenen kod ≤900 satır (`git diff --numstat`, `src/` içi, test hariç). Silinen satır bu tavandan düşülmez. Aşılırsa son eklenen parça kesilir.

## Geçiş ve arşiv

| Şey | Ne olur |
|---|---|
| 3.1.1 canlı kurulum | Adım 1 kapısına kadar çalışır; sonra `kur.ps1` ile değişir; `bin.onceki` 30 gün durur |
| `surum/3.1.1`, `surum/3.1.2` (yerel) | yerelde kalır, push edilmez (özel veri taraması yapılmadı); 30 gün sonra sahibin kararıyla arşiv etiketi |
| `knowledge/concepts` 1.153 not | dondurulur; `ara`da `arşiv` etiketiyle; yenisi üretilmez |
| `knowledge/hubs`, kök harita, CLAUDE.md `beyin:` bloğu | blok son kez "arşiv" notuyla güncellenir, sonra dokunulmaz |
| `Threads.md` | canlı konular karta (Adım 3), kalan `arsiv/` |
| `daily/` eski bloklar (3.x özetleri) | olduğu gibi kalır; `kayit` blokları yanına eklenir; aramada ikisi de |
| state.db, `bin.bak-*` 7 kopya, `%LOCALAPPDATA%\oom` 100+ klasör | sahibin kararıyla, arşiv anlık görüntüsünden sonra silinir |
| 245 worktree, 86 dal (yerel) | sahibin kararıyla; Defter dokunmaz |

## Yöntem kuralları

- Tek şerit (Codex ya da Claude), tek brief, tek kapı. Yargıç turu yalnız kapı düşerse.
- Brief'in ilk satırı OoM kartının yürürlük satırıdır.
- Her adım bitince: diff, ölçüm, bir cümle; Journal'a bir satır. Belge adım başına bir sayfa; bu belgenin ilgili bölümü güncellenir, yeni belge açılmaz.
- Üçüncü başarısız düzeltmeden sonra adım durur ve tasarım sorgulanır (Kural 8).
- Sahibin kota ve kullanım saatleri kısıt olarak kabul edilir: şerit gece koşabilir, kapı ölçümü sahibin gerçek oturumlarını bekler.

## Durdurma

- 3 ay içinde yeni bir sessiz arıza ("çalışıyor" denip çalışmıyor) çıkarsa Defter'in özel altyapısı kapatılır; ham kayıt ve kartlar elle sürer, `rg` arar. Bu şart yol haritasından aynen taşındı ve 5 Eki ile 10 Eki'de iki kez tetiklenmişti.
