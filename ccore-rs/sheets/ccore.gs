// Recebe os lotes do /audit-push do ccore (bot ou dashboard) e grava na
// EventLogs do mesmo jeito que o sendUpdate do EVENT FORM: quem falta entra na
// Roster, e o eventID e quantas vezes o jogador ja aparece naquele dia.
//
// Instalar: cole este arquivo como "ccore.gs" no projeto Apps Script da
// planilha. Em Configuracoes do projeto > Propriedades do script, crie
// CCORE_SECRET com o mesmo valor de audit.sheetWebhookSecret no config do bot.
// Depois: Implantar > Nova implantacao > App da Web, executar como "Eu" e
// acesso "Qualquer pessoa". A URL /exec vai em audit.sheetWebhookUrl.

function doPost(e) {
  let body;
  try {
    body = JSON.parse(e.postData.contents);
  } catch (err) {
    return reply({ ok: false, error: "JSON inválido." });
  }

  const props = PropertiesService.getScriptProperties();
  const secret = props.getProperty("CCORE_SECRET");
  if (!secret || body.secret !== secret) return reply({ ok: false, error: "Segredo inválido." });

  // Um push por vez: dois ao mesmo tempo calculariam a mesma linha vazia.
  const lock = LockService.getScriptLock();
  lock.waitLock(30000);
  try {
    return reply(appendBatches(body.batches || [], props));
  } finally {
    lock.releaseLock();
  }
}

function appendBatches(batches, props) {
  const sheet = SpreadsheetApp.getActiveSpreadsheet();
  const rosterSheet = sheet.getSheetByName("Roster");
  const logsSheet = sheet.getSheetByName("EventLogs");

  const rosterData = rosterSheet.getRange("D4:D").getValues().flat().filter(String);
  const added = [];

  // Quantas vezes cada jogador ja aparece em cada dia, para o eventID.
  const seen = {};
  const dayKey = (date, name) => date.toLocaleDateString() + "|" + name;
  logsSheet.getRange("B4:C").getValues().forEach(([date, name]) => {
    if (date !== "" && name !== "") {
      const k = dayKey(new Date(date), name);
      seen[k] = (seen[k] || 0) + 1;
    }
  });

  const rows = [];
  const done = [];
  for (const batch of batches) {
    // Lote ja recebido: um /audit-push refeito nao duplica linhas.
    if (props.getProperty("batch:" + batch.batchId)) continue;

    const [y, m, d] = batch.date.split("-").map(Number);
    const date = new Date(y, m - 1, d);

    for (const [name, kills, deaths, assists] of batch.rows) {
      if (!rosterData.includes(name)) {
        rosterSheet.getRange(`D${rosterData.length + 4}`).setValue(name);
        rosterData.push(name);
        added.push(name);
      }

      const k = dayKey(date, name);
      const eventId = seen[k] || 0;
      seen[k] = eventId + 1;
      rows.push([date, name, eventId, kills, deaths, assists]);
    }
    done.push(batch.batchId);
  }

  if (rows.length > 0) {
    const blankRow = logsSheet.getRange("B4").getNextDataCell(SpreadsheetApp.Direction.DOWN).getRow() + 1;
    logsSheet.getRange(blankRow, 2, rows.length, 6).setValues(rows);
    SpreadsheetApp.flush();
  }

  done.forEach((id) => props.setProperty("batch:" + id, new Date().toISOString()));
  return { ok: true, appended: rows.length, batches: done.length, addedToRoster: added };
}

function reply(obj) {
  return ContentService.createTextOutput(JSON.stringify(obj)).setMimeType(ContentService.MimeType.JSON);
}
