import { test, expect } from '@playwright/test'

test('import preserves duplicate properties and diagnostics locate case-insensitive and missing properties', async ({
  page,
}) => {
  const issues = [
    {
      code: 'duplicate_property',
      path: 'Fields[0].name',
      message: 'Свойство name повторяется.',
      hint: 'Оставьте одно значение.',
    },
    {
      code: 'constraint_type_mismatch',
      path: 'fields[0].minimum',
      message: 'Неверное ограничение.',
      hint: 'Используйте minLength.',
    },
    {
      code: 'customer_max_length',
      path: 'fields[0].maxLength',
      message: 'Не хватает maxLength.',
      hint: 'Добавьте maxLength.',
    },
  ]
  const text = '{"Fields":[{"name":"first","name":"second","Minimum":1}]}'
  await page.route('**/api/v1/**', async (route) => {
    const path = new URL(route.request().url()).pathname.replace('/api/v1', '')
    if (path === '/auth/me') return route.fulfill({ json: { id: 'user' } })
    if (path === '/auth/antiforgery') return route.fulfill({ json: { token: 'token' } })
    if (path === '/rule-definitions/validate') {
      expect(route.request().postData()).toBe(text)
      return route.fulfill({ status: 400, json: { code: 'invalid_rule_definition', issues } })
    }
    return route.fulfill({ json: { items: [], total: 0 } })
  })
  await page.goto('/rules')
  const editor = page.getByRole('textbox', { name: 'JSON правил' })
  await page
    .getByLabel('Загрузить JSON')
    .setInputFiles({
      name: 'duplicate.json',
      mimeType: 'application/json',
      buffer: Buffer.from(text),
    })
  await expect(page.getByRole('alert')).toContainText('Исправьте ошибки в определении: 3')
  await expect(editor).toHaveText(text)
  for (const [path, selected] of [
    ['Fields[0].name', '"name":"second"'],
    ['fields[0].minimum', '"Minimum":1'],
    ['fields[0].maxLength', '{"name":"first","name":"second","Minimum":1}'],
  ]) {
    await page.getByRole('button', { name: `Перейти к ошибке · ${path}`, exact: false }).click()
    await expect(editor).toBeFocused()
    expect(await page.evaluate(() => window.getSelection()?.toString())).toBe(selected)
  }
  // Tab leaves the code editor, keeping the diagnostic list accessible from the keyboard.
  await page.keyboard.press('Tab')
  await expect(
    page.getByRole('button', { name: /Перейти к ошибке.*Fields\[0\]\.name/ }),
  ).toBeFocused()
})

test('rule definition errors highlight the property, navigate, invalidate and clear after correction', async ({
  page,
}) => {
  let validations = 0
  let creates = 0
  const issue = {
    code: 'constraint_type_mismatch',
    path: 'fields[2].minimum',
    fieldName: 'description',
    message:
      'Свойство minimum задаёт числовую границу и применяется только к integer и decimal; указан тип string.',
    hint: 'Если нужно ограничить длину текста, замените minimum на minLength (целое число символов).',
  }
  await page.route('**/api/v1/**', async (route) => {
    const path = new URL(route.request().url()).pathname.replace('/api/v1', '')
    const json = (data: unknown, status = 200) => route.fulfill({ status, json: data })
    if (path === '/auth/me') return json({ id: 'user', email: 'user@example.test' })
    if (path === '/auth/antiforgery') return json({ token: 'test-token' })
    if (path === '/data-sources' || (path === '/rule-sets' && route.request().method() === 'GET'))
      return json({ items: [], total: 0 })
    if (path === '/rule-definitions/validate') {
      validations++
      const definition = route.request().postDataJSON()
      if (definition.fields[2]?.minimum !== undefined)
        return json(
          {
            code: 'invalid_rule_definition',
            issues: [issue],
            errors: { [issue.path]: [issue.message] },
          },
          400,
        )
      return json({ definition, contentHash: 'hash' })
    }
    if (path === '/rule-sets') {
      creates++
      // Exercise diagnostics returned during saving as well as the explicit preflight check.
      return json(
        {
          code: 'invalid_rule_definition',
          issues: [
            {
              ...issue,
              path: 'fields[2].minLength',
              message: 'Ошибка при сохранении определения.',
            },
          ],
        },
        400,
      )
    }
    return json({}, 404)
  })
  await page.goto('/rules')
  const editor = page.getByRole('textbox', { name: 'JSON правил' })
  const invalid = JSON.stringify(
    {
      fields: [
        { name: 'code' },
        { name: 'amount', type: 'decimal', minimum: 0 },
        { name: 'description', header: 'описание', type: 'string', required: true, minimum: 1 },
      ],
    },
    null,
    2,
  )
  await editor.fill(invalid)
  await page.getByRole('button', { name: 'Проверить определение' }).click()
  const errors = page.getByRole('alert')
  await expect(errors).toContainText('замените minimum на minLength')
  await expect(errors).toContainText('description')
  await expect(page.locator('.cm-lintRange-error')).toContainText('"minimum": 1')
  await page.getByRole('button', { name: /Перейти к ошибке.*fields\[2\]\.minimum/ }).click()
  await expect(editor).toBeFocused()
  expect(await page.evaluate(() => window.getSelection()?.toString())).toBe('"minimum": 1')
  await page.getByLabel('Название', { exact: true }).fill('Правила')
  await page.getByRole('button', { name: 'Создать новый набор' }).click()
  await expect(errors).toContainText('minLength')
  expect(creates).toBe(0)
  await editor.fill(invalid.replace('"minimum": 1', '"minLength": 1'))
  await expect(page.getByText('Определение изменено. Повторите проверку.')).toBeVisible()
  await expect(page.locator('.cm-lintRange-error')).toHaveCount(0)
  await page.getByRole('button', { name: 'Проверить определение' }).click()
  await expect(page.getByText('Определение корректно', { exact: true })).toBeVisible()
  await expect(errors).toHaveCount(0)
  await page.getByRole('button', { name: 'Создать новый набор' }).click()
  await expect(errors).toContainText('Ошибка при сохранении определения.')
  expect(creates).toBe(1)
  const beforeSyntax = validations
  await editor.fill('{"fields": [}')
  await expect(errors).toContainText('Исправьте синтаксис')
  await page.getByRole('button', { name: 'Проверить определение' }).click()
  expect(validations).toBe(beforeSyntax)
  await expect(errors).toContainText('строка 1')
  await page
    .getByLabel('Загрузить JSON')
    .setInputFiles({ name: 'bad.json', mimeType: 'application/json', buffer: Buffer.from('{') })
  await expect(editor).toHaveText('{')
  await expect(errors).toContainText('Исправьте синтаксис')
  expect(await page.evaluate(() => document.documentElement.scrollWidth > window.innerWidth)).toBe(
    false,
  )
})

test('rules view displays granular valid/upsert counters and surfaces Problem Details detail', async ({
  page,
}) => {
  const setId = '11111111-1111-4111-8111-111111111111'
  const versionId = '22222222-2222-4222-8222-222222222222'
  const sourceId = '33333333-3333-4333-8333-333333333333'
  const tabularJobId = '44444444-4444-4444-8444-444444444444'
  const customerJobId = '55555555-5555-4555-8555-555555555555'
  let uploadCount = 0

  await page.route('**/api/v1/**', async (route) => {
    const request = route.request()
    const path = new URL(request.url()).pathname.replace('/api/v1', '')
    const json = (data: unknown, status = 200) => route.fulfill({ status, json: data })

    if (path === '/auth/me') return json({ id: 'user', email: 'user@example.test' })
    if (path === '/auth/antiforgery') return json({ token: 'test-token' })
    if (path === '/rule-sets' && request.method() === 'GET')
      return json({
        items: [{ id: setId, name: 'Заказы', revision: 1, description: null, isArchived: false }],
        total: 1,
      })
    if (path === `/rule-sets/${setId}/versions`)
      return json({
        items: [{ id: versionId, ruleSetId: setId, name: 'Заказы', versionNumber: 3 }],
        total: 1,
      })
    if (path === `/rule-set-versions/${versionId}`)
      return json({
        id: versionId,
        ruleSetId: setId,
        name: 'Заказы',
        versionNumber: 3,
        definition: { fields: [{ name: 'code' }] },
      })
    if (path === '/data-sources')
      return json({ items: [{ id: sourceId, name: 'Тестовый источник' }], total: 1 })
    if (path === `/rule-set-versions/${versionId}/publication`)
      return json(
        {
          status: 400,
          title: 'Request validation failed.',
          detail: 'Публикация временно запрещена политикой организации.',
          code: 'validation_failed',
        },
        400,
      )
    if (path === `/data-sources/${sourceId}/files`) {
      uploadCount++
      return json({ jobId: uploadCount === 1 ? tabularJobId : customerJobId }, 202)
    }
    if (path === `/jobs/${tabularJobId}`)
      return json({
        id: tabularJobId,
        dataSourceId: sourceId,
        originalFileName: 'tabular.csv',
        status: 'CompletedWithErrors',
        schemaVersion: 1,
        ruleVersionNumber: 3,
        createdAt: '2026-11-10T12:00:00Z',
        totalRows: 10,
        processedRows: 10,
        validRows: 8,
        insertedRows: 0,
        updatedRows: 0,
        duplicateRows: 0,
        invalidRows: 2,
        canCancel: false,
        canRetry: false,
        cancelRequested: false,
      })
    if (path === `/jobs/${customerJobId}`)
      return json({
        id: customerJobId,
        dataSourceId: sourceId,
        originalFileName: 'customers.csv',
        status: 'CompletedWithErrors',
        schemaVersion: 1,
        ruleVersionNumber: 3,
        createdAt: '2026-11-10T12:00:00Z',
        totalRows: 9,
        processedRows: 9,
        validRows: 0,
        insertedRows: 5,
        updatedRows: 2,
        duplicateRows: 1,
        invalidRows: 1,
        canCancel: false,
        canRetry: false,
        cancelRequested: false,
      })
    return json({}, 404)
  })

  await page.goto('/rules')
  await page.getByRole('button', { name: 'Заказы', exact: true }).click()
  await page.getByRole('button', { name: /Заказы · v3/ }).click()

  // Problem Details `detail` is preferred over generic `title`
  await page.getByRole('button', { name: 'Опубликовать версию' }).click()
  await expect(page.getByRole('alert')).toHaveText(
    'Публикация временно запрещена политикой организации.',
  )

  // 1. Tabular job shows explicit Валидно counter and breakdown
  await page.getByRole('combobox', { name: 'Источник' }).selectOption(sourceId)
  await page.getByLabel('Файл (CSV или XLSX)').setInputFiles({
    name: 'tabular.csv',
    mimeType: 'text/csv',
    buffer: Buffer.from('code\nA\n'),
  })
  await page.getByRole('button', { name: 'Начать проверку' }).click()
  const summary = page.locator('.job-summary')
  await expect(summary).toContainText('Проверено 10 из 10')
  await expect(summary).toContainText('Принято 8 (Валидно 8)')
  await expect(summary).toContainText('Ошибочных 2')
  await expect(summary).toContainText('Прошли проверку')

  // 2. Customer job shows Inserted / Updated / Duplicate breakdown
  await page.getByRole('button', { name: 'Начать проверку' }).click()
  await expect(summary).toContainText('Проверено 9 из 9')
  await expect(summary).toContainText('Принято 8 (Добавлено 5 · Обновлено 2 · Без изменений 1)')
  await expect(summary).toContainText('Ошибочных 1')
})
