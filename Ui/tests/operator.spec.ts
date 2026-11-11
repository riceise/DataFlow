import { test, expect } from '@playwright/test'

test('source → upload → row errors → CSV; unknown total and network recovery', async ({ page }) => {
  const sourceId = '971fbdd8-aa18-4121-8cad-8cf4ee18fe23'
  const jobId = 'dd78b18c-7d9d-4c93-843f-bb77bfae0068'
  const sources: Record<string, unknown>[] = []
  let finished = false,
    offline = false,
    uploaded = false
  const schema = {
    version: 1,
    keyFields: ['externalId'],
    csvDelimiter: ',',
    fields: [{ name: 'externalId', type: 'string', required: true, maxLength: 80 }],
  }
  await page.route('**/api/v1/**', async (route) => {
    const request = route.request(),
      url = new URL(request.url()),
      path = url.pathname.replace('/api/v1', '')
    const json = (data: unknown, status = 200) => route.fulfill({ status, json: data })
    if (path === '/auth/me') return json({ id: 'user', email: 'operator@example.test' })
    if (path === '/auth/antiforgery') return json({ token: 'test-token' })
    if (path === '/rule-library') return json({ items: [], page: 1, pageSize: 200, total: 0 })
    if (path === '/files/options') return json({ maxFileBytes: 52428800, formats: ['Csv', 'Xlsx'] })
    if (path === '/data-sources' && request.method() === 'POST') {
      const body = request.postDataJSON()
      sources.push({
        id: sourceId,
        name: body.name,
        dataType: 'Customer',
        schema: body.schema,
        schemaVersion: 1,
        acceptedFormats: body.acceptedFormats,
        isActive: true,
        updatedAt: '2026-11-10T12:00:00Z',
      })
      return json(sources[0], 201)
    }
    if (path === '/data-sources')
      return json({ items: sources, total: sources.length, page: 1, pageSize: 200 })
    if (path === `/data-sources/${sourceId}`) return json(sources[0])
    if (path.endsWith('/files')) {
      expect(request.headers()['idempotency-key']).toBeTruthy()
      expect(request.headers()['x-csrf-token']).toBe('test-token')
      uploaded = true
      return json({ jobId, status: 'Queued' }, 202)
    }
    if (path === `/jobs/${jobId}`) {
      if (offline) return route.abort('internetdisconnected')
      return json({
        id: jobId,
        dataSourceId: sourceId,
        originalFileName: 'customers.csv',
        status: finished ? 'CompletedWithErrors' : 'Processing',
        schemaVersion: 1,
        createdAt: '2026-11-10T12:00:00Z',
        totalRows: finished ? 2 : null,
        processedRows: finished ? 2 : 1,
        insertedRows: 1,
        updatedRows: 0,
        invalidRows: finished ? 1 : 0,
        duplicateRows: 0,
        validRows: 0,
        canCancel: !finished,
        canRetry: false,
        cancelRequested: false,
      })
    }
    if (path.endsWith('/rows'))
      return json({
        items: finished
          ? [
              {
                rowNumber: 3,
                status: 'Invalid',
                rawData: '["b","bad-email"]',
                naturalKey: null,
                errors: [
                  { field: 'email', code: 'invalid_email', message: 'Укажите полный email.' },
                  { field: 'name', code: 'required', message: 'Укажите имя.' },
                ],
              },
            ]
          : [],
        total: finished ? 1 : 0,
        page: 1,
        pageSize: 25,
      })
    if (path.endsWith('/errors.csv'))
      return route.fulfill({
        contentType: 'text/csv',
        body: 'rowNumber,field,code,message,rawData\r\n3,email,invalid_email,Invalid,[]\r\n',
      })
    if (path.endsWith('/rule-definition')) return json(schema)
    return json({ title: `Unexpected ${path}` }, 404)
  })
  await page.goto('/sources/new')
  await page.getByLabel('Название', { exact: true }).fill('Клиенты тест')
  await page.getByRole('button', { name: 'Сохранить', exact: true }).click()
  await expect(page.getByRole('heading', { name: 'Клиенты тест' })).toBeVisible()
  await page.getByRole('link', { name: 'Загрузить файл', exact: true }).click()
  await page
    .getByLabel('Файл CSV или XLSX')
    .setInputFiles({
      name: 'customers.csv',
      mimeType: 'text/csv',
      buffer: Buffer.from(
        'externalId,name,email,isActive\na,A,a@example.test,true\nb,,bad-email,true',
      ),
    })
  await page.getByRole('button', { name: 'Загрузить и начать импорт' }).click()
  await expect(page.getByRole('heading', { name: 'customers.csv' })).toBeVisible()
  expect(uploaded).toBe(true)
  await expect(page.getByText('Всего строк станет известно после чтения файла.')).toBeVisible()
  await expect(page.getByRole('progressbar')).not.toHaveAttribute('aria-valuenow')
  offline = true
  await expect(page.getByRole('alert')).toContainText('Связь потеряна', { timeout: 10000 })
  await expect(page.getByText('Всего строк станет известно после чтения файла.')).toBeVisible()
  offline = false
  finished = true
  await page.getByRole('button', { name: 'Повторить сейчас' }).click()
  await expect(page.getByText('Завершён с ошибками строк', { exact: true })).toBeVisible()
  await expect(page.getByRole('button', { name: 'Запросить отмену' })).toHaveCount(0)
  await page.getByRole('button', { name: 'Раскрыть', exact: true }).click()
  await expect(page.getByText('invalid_email', { exact: true })).toBeVisible()
  await expect(page.getByText('required', { exact: true })).toBeVisible()
  const download = page.waitForEvent('download')
  await page.getByRole('button', { name: 'Скачать ошибки CSV' }).click()
  expect((await download).suggestedFilename()).toBe(`import-${jobId}-errors.csv`)
  const overflow = await page.evaluate(
    () => document.documentElement.scrollWidth > window.innerWidth,
  )
  expect(overflow).toBe(false)
})

test('404 and keyboard-accessible login', async ({ page }) => {
  await page.goto('/missing-route')
  await expect(page.getByRole('heading', { name: 'Страница не найдена' })).toBeVisible()
  await page.goto('/login')
  await page.getByLabel('Email', { exact: true }).focus()
  await page.keyboard.type('operator@example.test')
  await page.keyboard.press('Tab')
  await expect(page.getByLabel('Пароль', { exact: true })).toBeFocused()
})
