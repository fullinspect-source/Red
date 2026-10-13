from pathlib import Path
import unittest
ROOT = Path(__file__).resolve().parents[1]

class CommentSnippetSourceTests(unittest.TestCase):
    def test_shared_readonly_surface(self):
        helper = (ROOT / 'Services/ChecklistCommentSnippet.cs').read_text()
        for required in ['TextWrapping.NoWrap', 'TextTrimming.CharacterEllipsis', 'Focusable = false', 'BindingMode.OneWay', 'nameof(Item.Comments)', 'ToolTipProperty', 'Visibility.Collapsed']:
            self.assertIn(required, helper)
        self.assertNotIn('TextBox', helper)
        self.assertNotIn('Save(', helper)
        self.assertNotIn('Populate', helper)
    def test_both_renderers_and_prompt_width(self):
        source = (ROOT / 'MainWindow.xaml.cs').read_text()
        self.assertEqual(source.count('ChecklistCommentSnippet.Create(item, _checklistFontSize)'), 2)
        header = source.split('private Grid CreateInlineItemHeader')[1].split('private ', 1)[0]
        self.assertIn('Grid.SetRow(snippet, 1)', header)
        self.assertIn('Grid.SetColumn(snippet, 4)', header)
        self.assertNotIn('Text = item.Comments.Trim()', header)
        self.assertIn('Grid.SetColumnSpan(prompt, IsSpecialInlineDrawerRemembered(item, InlineNumberpadDrawer) ? 1 : 2)', header)
        self.assertIn('MinWidth = 180', header)
    def test_comment_notification_is_model_owned(self):
        model = (ROOT / 'Models/InspectionModels.cs').read_text()
        self.assertIn('class Item : System.ComponentModel.INotifyPropertyChanged', model)
        self.assertIn('new System.ComponentModel.PropertyChangedEventArgs(nameof(Comments))', model)
        source = (ROOT / 'MainWindow.xaml.cs').read_text()
        for method in ['InlineCommentsBox_TextChanged', 'CommentsTextBox_TextChanged']:
            body = source.split('private void ' + method)[1].split('private ', 1)[0]
            self.assertIn('CaptureCommentEdit(', body)
            self.assertNotIn('Populate', body)

if __name__ == '__main__':
    unittest.main()
