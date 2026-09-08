type Callback = for<'a> fn(#[cfg(enabled)] value: &'a str, _: i32) -> bool;
type Closure = dyn for<'b> Fn(&'b str) -> usize + 'static;
type Opaque = impl Copy + 'static;
type A<T> = Outer<'static, T, 3, { 1 + 2 }, -1, Item<'static>=u8, Item: Copy>;
