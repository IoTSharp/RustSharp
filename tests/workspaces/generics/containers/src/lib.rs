pub trait Allowed {}

impl Allowed for i32 {}

pub struct Container<T> {
    pub value: T,
}

pub fn make<T: Allowed>(value: T) -> Container<T> {
    Container { value }
}

pub fn unwrap<T>(container: Container<T>) -> T {
    container.value
}
