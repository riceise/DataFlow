import { findNodeAtLocation, parseTree, printParseErrorCode, type ParseError } from 'jsonc-parser'
import type { RuleDefinitionIssue } from '@/api'

export type DefinitionDiagnostic = RuleDefinitionIssue & { from: number; to: number }
const options = { allowTrailingComma: false, disallowComments: true, allowEmptyContent: false }
const syntaxMessages: Record<string, string> = {
  InvalidSymbol: 'Недопустимый символ. Проверьте кавычки и значения.',
  PropertyNameExpected: 'Ожидается имя свойства в двойных кавычках.',
  ValueExpected: 'Ожидается значение после двоеточия или запятой.',
  ColonExpected: 'После имени свойства нужно двоеточие «:».',
  CommaExpected: 'Между свойствами или элементами нужна запятая «,».',
  CloseBraceExpected: 'Не хватает закрывающей фигурной скобки «}».',
  CloseBracketExpected: 'Не хватает закрывающей квадратной скобки «]».',
  EndOfFileExpected: 'После JSON-объекта есть лишние символы.',
  InvalidCommentToken: 'Комментарии в JSON не поддерживаются.',
  UnexpectedEndOfString: 'Строка не закрыта двойной кавычкой.',
  InvalidEscapeCharacter: 'Недопустимая escape-последовательность в строке.',
  InvalidUnicode: 'Некорректная Unicode-последовательность в строке.',
  UnexpectedEndOfNumber: 'Число записано не полностью.',
  InvalidCharacter: 'Недопустимый символ внутри строки.',
}

export function syntaxDiagnostics(text: string): DefinitionDiagnostic[] {
  const errors: ParseError[] = []
  parseTree(text, errors, options)
  return errors.map((error) => ({
    code: 'invalid_json',
    path: '$',
    message: syntaxMessages[printParseErrorCode(error.error)] ?? 'Некорректный синтаксис JSON.',
    hint: 'Исправьте синтаксис в отмеченном месте и повторите проверку.',
    from: error.offset,
    to: Math.min(text.length, error.offset + Math.max(error.length, 1)),
  }))
}

export function locateIssues(text: string, issues: RuleDefinitionIssue[]): DefinitionDiagnostic[] {
  const root = parseTree(text, [], options)
  return issues.map((issue) => {
    const path = [...issue.path.replace(/^\$\.?/, '').matchAll(/([^.[\]]+)|\[(\d+)\]/g)].map(
      (part) => (part[2] === undefined ? part[1]! : Number(part[2])),
    )
    // The server accepts property names case-insensitively; preserve the spelling in the editor.
    let node = root
    let nearest = root
    for (const segment of path) {
      if (typeof segment === 'string' && node?.type === 'object') {
        const properties = node.children ?? []
        // Duplicate-property diagnostics point at the conflicting occurrence.
        const candidates =
          issue.code === 'duplicate_property' ? [...properties].reverse() : properties
        node = candidates.find(
          (property) =>
            String(property.children?.[0]?.value).toLowerCase() === segment.toLowerCase(),
        )?.children?.[1]
      } else node = node && findNodeAtLocation(node, [segment])
      if (!node) break
      nearest = node
    }
    // Missing properties are reported on their nearest existing parent.
    node ??= nearest
    const target = node?.parent?.type === 'property' ? node.parent : node
    const from = target?.offset ?? 0
    return { ...issue, from, to: Math.min(text.length, from + (target?.length ?? text.length)) }
  })
}

export function diagnosticPosition(text: string, offset: number) {
  const before = text.slice(0, offset)
  return { line: before.split('\n').length, column: offset - before.lastIndexOf('\n') }
}
