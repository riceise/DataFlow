import { test, expect } from '@playwright/test'

// Requires API + Worker + infrastructure. Each run owns its synthetic user/data.
test('real cookie/CSRF session and Customer import through Worker', async ({ page }) => {
  test.setTimeout(180000)
  const email = `ui-${crypto.randomUUID()}@example.test`
  await page.goto('/login')
  await page.getByRole('button', { name: 'Создать аккаунт' }).click()
  await page.getByLabel('Email', { exact: true }).fill(email)
  await page.getByLabel('Пароль', { exact: true }).fill('DataFlow-Test-2026!')
  await page.getByRole('button', { name: 'Зарегистрироваться', exact: true }).click()
  await expect(page.getByRole('heading', { name: 'Обзор', exact: true })).toBeVisible()
  await page.goto('/sources/new')
  await page.getByLabel('Название', { exact: true }).fill('Синтетические клиенты UI')
  await page.getByRole('button', { name: 'Сохранить', exact: true }).click()
  await expect(page.getByRole('heading', { name: 'Синтетические клиенты UI' })).toBeVisible()
  await page.getByRole('link', { name: 'Загрузить файл', exact: true }).click()
  await page.getByLabel('Файл CSV или XLSX').setInputFiles({
    name: 'operator-fixture.csv', mimeType: 'text/csv',
    buffer: Buffer.from('externalId,name,email,registeredAt,isActive\nui-1,Первый,first@example.test,2026-01-01,true\nui-2,,invalid,2026-01-01,true\n'),
  })
  await page.getByRole('button', { name: 'Загрузить и начать импорт' }).click()
  await expect(page.getByRole('heading', { name: 'operator-fixture.csv' })).toBeVisible()
  await expect(page.getByText('Завершён с ошибками строк', { exact: true })).toBeVisible({ timeout: 60000 })
  const statusFilter = page.getByRole('combobox', { name: 'Результат' })
  await statusFilter.selectOption('Invalid')
  await expect(statusFilter).toHaveValue('Invalid')
  await expect(page.getByRole('row', { name: /Добавлено/ })).toHaveCount(0)
  await page
    .getByRole('row', { name: /С ошибкой/ })
    .getByRole('button', { name: 'Раскрыть', exact: true })
    .click()
  await expect(page.getByText('required', { exact: true })).toBeVisible()
  const download = page.waitForEvent('download')
  await page.getByRole('button', { name: 'Скачать ошибки CSV' }).click()
  expect((await download).suggestedFilename()).toContain('errors.csv')
  await page.goto('/rules')
  await expect(page.getByRole('heading', { name: 'Библиотека правил' })).toBeVisible()
  await expect(page.getByRole('button', { name: 'Проверить определение' })).toBeVisible()
})
