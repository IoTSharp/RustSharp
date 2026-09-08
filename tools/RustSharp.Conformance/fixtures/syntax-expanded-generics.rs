struct Buffer<'a: 'b, 'b, T: ?Sized + 'a = u8, const N: usize = { 2 + 2 }> where T: Copy, 'a: 'b { value: &'a T, data: [u8; N] }
fn project<T>() where for<'a> &'a T: Into<u8>, <T as Trait>::Item: Copy {}
enum E { #[marker] Empty {}, Tuple(), Named { #[field] key: i32 }, Unit = -1, Next = 2 + 3, }
