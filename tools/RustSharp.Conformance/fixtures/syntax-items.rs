struct Unit;
struct Empty {}
pub struct Tuple<T>(pub T, i32);
enum Choice<T> { None, Empty(), One(T), Pair(T, T), }
type Count = usize;
pub const LIMIT: usize = 4;
