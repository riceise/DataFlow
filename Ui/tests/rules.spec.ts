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
